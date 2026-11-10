using LucidStream.Core.Common;
using LucidStream.Core.Devices;

namespace LucidStream.Core.Cloud;

/// <summary>Where a Cloud recording goes: the org and patient picked in the Cloud viewer.</summary>
public sealed record CloudTarget(string OrgId, string SubOrgId, string SubjectId, string OrgName, string SubjectName);

public enum CloudRecState { Recording, Finishing, Done, Error }

/// <summary>A snapshot for the UI.</summary>
public sealed record CloudRecorderStatus(
    CloudRecState State,
    string Target,
    string? CloudSessionId,
    int ChunksWaiting,
    int ChunksVerified,
    double? CloudBehindSeconds,
    double? LastChunkUploadSeconds,
    string? Problem);

/// <summary>
/// Records the patch's raw packets to Lucidify Cloud as a new session for one patient, the way the Android app does:
/// 5-second chunks of unchanged 36-byte packets (2500 per chunk), spooled encrypted on disk first, then uploaded
/// (chunk init, PUT to the signed URL, complete with SHA-256 check), newest chunk first so the Cloud view stays live
/// and older ones backfilled. Markers go up as session events. Stopping flushes the last chunk, then finalizes once
/// every chunk is verified (finalization intent, finalize, manifest readback). A recording interrupted by closing the
/// app is finished on the next start (<see cref="Resume"/>).
///
/// Sample indexes follow the patch's packet counter, so packets lost on the link leave a gap in the session timeline
/// (a chunk ends early and the next one starts at the right index) instead of shifting later data.
/// </summary>
public sealed class CloudRecorder : IAsyncDisposable
{
    public const int SampleRateHz = 500;
    public const int ChunkSeconds = 5;
    public const int ChunkPackets = SampleRateHz * ChunkSeconds;

    /// <summary>Channel labels sent at session init, identical to the Android app's so Cloud tools treat both alike.</summary>
    public static readonly string[] Channels = { "Fp2-AFz", "-Fp2", "AFz", "Imp1", "Imp2", "AccX", "AccY", "AccZ" };

    private readonly CloudApi _api;
    private readonly CloudSpool _spool;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private SpoolSession _session;
    private Task? _loop;

    // chunk being assembled (acquisition thread)
    private readonly MemoryStream _chunk = new();
    private long _chunkFirst = -1;
    private int _chunkCount;
    private long? _baseIndex;
    private long _localEnd;
    private bool _stopped;

    // status
    private int _waiting, _verified;
    private double? _behind, _lastUpload;
    private string? _problem;
    private CloudRecState _state;
    private long? _cloudThrough;

    private CloudRecorder(CloudApi api, CloudSpool spool, SpoolSession session, bool resumed)
    {
        _api = api;
        _spool = spool;
        _session = session;
        _state = resumed ? CloudRecState.Finishing : CloudRecState.Recording;
        _stopped = resumed;
    }

    /// <summary>Raised once Cloud has created the session (sessionId), so the viewer can open it.</summary>
    public event Action<string, CloudTarget>? SessionBound;

    /// <summary>Raised when the recording is finalized or has failed for good.</summary>
    public event Action<CloudRecorderStatus>? Ended;

    public CloudTarget Target => new(_session.OrgId, _session.SubOrgId, _session.SubjectId, _session.OrgName, _session.SubjectName);

    /// <summary>Start a new Cloud recording. Packets are taken from the next <see cref="OnRaw"/> call on.</summary>
    public static CloudRecorder Start(CloudApi api, CloudTarget target, string spoolRoot, ISpoolProtector protector)
    {
        var id = Guid.NewGuid().ToString();
        var spool = new CloudSpool(Path.Combine(spoolRoot, id), protector);
        var session = new SpoolSession
        {
            LocalSessionId = id,
            OrgId = target.OrgId,
            SubOrgId = target.SubOrgId,
            SubjectId = target.SubjectId,
            OrgName = target.OrgName,
            SubjectName = target.SubjectName,
            StartedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        spool.SaveSession(session);
        var r = new CloudRecorder(api, spool, session, resumed: false);
        r._loop = Task.Run(r.RunAsync);
        Log.Info($"Cloud recording started for {target.SubjectName} ({target.OrgName}).");
        return r;
    }

    /// <summary>Finish a recording left in the spool by an earlier run (uploads what is left, then finalizes).</summary>
    public static CloudRecorder Resume(CloudApi api, string folder, ISpoolProtector protector)
    {
        var spool = new CloudSpool(folder, protector);
        var session = spool.LoadSession();
        if (session.FinalSeqExclusive == null)
        {
            var chunks = spool.Chunks();
            session.FinalSeqExclusive = chunks.Count == 0 ? 0 : chunks.Max(c => c.Seq) + 1;
            session.NextSeq = session.FinalSeqExclusive.Value;
            var lastEnd = chunks.Count == 0 ? 0 : chunks.Max(c => c.TEndMs);
            session.EndedAtEpochMs = session.StartedAtEpochMs + lastEnd;
            spool.SaveSession(session);
        }
        var r = new CloudRecorder(api, spool, session, resumed: true);
        r._loop = Task.Run(r.RunAsync);
        Log.Info($"Finishing an earlier Cloud recording for {session.SubjectName} ({session.OrgName}).");
        return r;
    }

    public CloudRecorderStatus Status
    {
        get
        {
            lock (_gate)
                return new CloudRecorderStatus(_state, $"{_session.SubjectName} ({_session.OrgName})", _session.CloudSessionId,
                    _waiting, _verified, _behind, _lastUpload, _problem);
        }
    }

    // ------------------------------------------------------------------ acquisition side

    /// <summary>Raw packets from the patch. Called on the acquisition thread; writes a chunk to disk every 5 s.</summary>
    public void OnRaw(CgxRawBlock block)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _baseIndex ??= block.FirstSampleIndex;
            var index = block.FirstSampleIndex - _baseIndex.Value;
            var n = block.Count;
            for (var i = 0; i < n; i++)
            {
                var sample = index + i;
                if (_chunkCount > 0 && sample != _chunkFirst + _chunkCount) FlushChunk();
                if (_chunkCount == 0) _chunkFirst = sample;
                _chunk.Write(block.Packets, i * CgxPatchFormat.PacketSize, CgxPatchFormat.PacketSize);
                _chunkCount++;
                if (_chunkCount == ChunkPackets) FlushChunk();
            }
            _localEnd = index + n;
            UpdateBehind();
        }
    }

    /// <summary>A marker at the current position.</summary>
    public void AddMarker(string label)
    {
        lock (_gate)
        {
            if (_stopped || string.IsNullOrWhiteSpace(label)) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sample = Math.Max(0, _localEnd);
            _session.PendingEvents.Add(new CloudEvent(Guid.NewGuid().ToString(), "TEXT", label.Trim(), sample, sample * 1000 / SampleRateHz, now, now));
            _spool.SaveSession(_session);
        }
        _wake.Release();
    }

    /// <summary>Stop recording: write the last partial chunk and let the uploader finish and finalize.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            FlushChunk();
            _stopped = true;
            _session.FinalSeqExclusive = _session.NextSeq;
            _session.EndedAtEpochMs = _session.StartedAtEpochMs + _localEnd * 1000 / SampleRateHz;
            _spool.SaveSession(_session);
            _state = CloudRecState.Finishing;
        }
        Log.Info($"Cloud recording stopped after {_localEnd / (double)SampleRateHz:0} s; finishing the upload.");
        _wake.Release();
    }

    /// <summary>Wait until the recording is finalized (or failed), at most <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitFinishedAsync(TimeSpan timeout)
    {
        if (_loop == null) return true;
        return await Task.WhenAny(_loop, Task.Delay(timeout)) == _loop;
    }

    private void FlushChunk()
    {
        if (_chunkCount == 0) return;
        var seq = _session.NextSeq;
        try
        {
            _spool.WriteChunk(seq, _chunkFirst, _chunkCount, _chunk.ToArray());
            _session.NextSeq = seq + 1;
            _spool.SaveSession(_session);
            _waiting++;
        }
        catch (Exception ex)
        {
            _problem = $"Could not save a chunk on this PC: {ex.Message}";
            Log.Error(_problem);
        }
        _chunk.SetLength(0);
        _chunkCount = 0;
        _wake.Release();
    }

    private void UpdateBehind()
    {
        if (_cloudThrough is long through) _behind = Math.Max(0, _localEnd - (through + 1)) / (double)SampleRateHz;
    }

    // ------------------------------------------------------------------ upload side

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        var backoff = TimeSpan.FromSeconds(1);
        var lastCoverage = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_session.CloudSessionId == null) await InitSessionAsync(ct);

                var chunks = _spool.Chunks();
                lock (_gate)
                {
                    _waiting = chunks.Count(c => !c.Verified);
                    _verified = chunks.Count(c => c.Verified);
                }

                var next = PickNext(chunks);
                if (next != null)
                {
                    await UploadAsync(next, ct);
                    backoff = TimeSpan.FromSeconds(1);
                    lock (_gate) _problem = null;
                    continue;
                }

                if (_session.PendingEvents.Count > 0) await PostEventsAsync(ct);

                if (DateTime.UtcNow - lastCoverage > TimeSpan.FromSeconds(3) && _verified > 0)
                {
                    lastCoverage = DateTime.UtcNow;
                    var cov = await _api.CoverageAsync(_session.OrgId, _session.SubjectId, _session.CloudSessionId!, ct);
                    lock (_gate)
                    {
                        _cloudThrough = cov.ContiguousThroughSample;
                        UpdateBehind();
                    }
                }

                bool stopped;
                lock (_gate) stopped = _stopped;
                if (stopped)
                {
                    if (chunks.Any(c => c.Failed))
                    {
                        Fail("A chunk was refused by Cloud, so the session cannot be finalized. See the log.");
                        return;
                    }
                    if (chunks.All(c => c.Verified))
                    {
                        await FinalizeAsync(ct);
                        return;
                    }
                }

                await _wake.WaitAsync(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (CloudApiException ex) when (ex.AuthRequired)
            {
                lock (_gate) _problem = "Sign in to Lucidify Cloud again (Cloud page). Upload resumes after sign-in.";
                await Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (Exception ex) when (ex is CloudApiException { Retryable: true } or IOException or InvalidDataException or HttpRequestException)
            {
                lock (_gate) _problem = $"Upload paused, retrying: {ex.Message}";
                Log.Warn($"Cloud upload: {ex.Message}; retrying in {backoff.TotalSeconds:0} s.");
                await Delay(backoff, ct);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
            catch (Exception ex)
            {
                if (_session.CloudSessionId == null)
                {
                    Fail($"Cloud would not create the session: {ex.Message}");
                    return;
                }
                lock (_gate) _problem = ex.Message;
                Log.Error($"Cloud upload: {ex.Message}");
                await Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    private static async Task Delay(TimeSpan t, CancellationToken ct)
    {
        try { await Task.Delay(t, ct); }
        catch (OperationCanceledException) { }
    }

    /// <summary>Newest chunk first while recording, so Cloud shows the live edge; then the backlog oldest first.</summary>
    private SpoolChunk? PickNext(List<SpoolChunk> chunks)
    {
        var pending = chunks.Where(c => !c.Verified && !c.Failed).ToList();
        if (pending.Count == 0) return null;
        bool stopped;
        lock (_gate) stopped = _stopped;
        var newest = pending[^1];
        return !stopped && newest.Seq == _session.NextSeq - 1 ? newest : pending[0];
    }

    private async Task InitSessionAsync(CancellationToken ct)
    {
        var init = await _api.SessionInitAsync(_session.OrgId, _session.SubjectId, _session.LocalSessionId, _session.StartedAtEpochMs,
            SampleRateHz, Channels, ChunkSeconds, ct);
        lock (_gate)
        {
            _session.CloudSessionId = init.SessionId;
            _spool.SaveSession(_session);
        }
        Log.Info($"Cloud session {init.SessionId} created{(init.Reused ? " (reused)" : "")}.");
        SessionBound?.Invoke(init.SessionId, Target);
    }

    private async Task UploadAsync(SpoolChunk c, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var key = $"{_session.LocalSessionId}:{c.Seq}";
        var sid = _session.CloudSessionId!;
        try
        {
            if (!c.PutDone || c.ChunkId == null)
            {
                var payload = _spool.ReadPayload(c);
                var init = await _api.ChunkInitAsync(_session.OrgId, _session.SubjectId, sid, key, c.Seq, c.FirstSampleIndex, c.SampleCount,
                    c.TStartMs, c.TEndMs, c.Bytes, c.Sha256, ct);
                c.ChunkId = init.ChunkId;
                if (init.AlreadyVerified)
                {
                    c.Verified = true;
                    _spool.SaveChunk(c);
                    return;
                }
                if (init.UploadRequired)
                {
                    if (string.IsNullOrEmpty(init.SignedPutUrl)) throw new CloudApiException(500, "Cloud asked for an upload without a signed URL.");
                    await CloudApi.PutSignedAsync(init.SignedPutUrl, payload, ct);
                }
                c.PutDone = true;
                _spool.SaveChunk(c);
            }

            var done = await _api.ChunkCompleteAsync(_session.OrgId, _session.SubjectId, sid, c.ChunkId!, key, c.Bytes, c.Sha256, ct);
            if (!done.Verified) throw new CloudApiException(503, $"Cloud has not verified chunk {c.Seq} yet ({done.Status}).");
            if (done.Bytes is long b && b != c.Bytes) throw new InvalidOperationException($"Cloud byte count differs for chunk {c.Seq} ({b} vs {c.Bytes}).");
            if (done.Sha256 != null && done.Sha256 != c.Sha256) throw new InvalidOperationException($"Cloud checksum differs for chunk {c.Seq}.");
            c.Verified = true;
            c.LastError = null;
            _spool.SaveChunk(c);
            lock (_gate) _lastUpload = (DateTime.UtcNow - started).TotalSeconds;
        }
        catch (CloudApiException ex) when (!ex.Retryable && !ex.AuthRequired)
        {
            c.Failed = true;
            c.LastError = ex.Message;
            _spool.SaveChunk(c);
            Log.Error($"Cloud refused chunk {c.Seq}: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            // Verification mismatch: upload it again from scratch.
            c.PutDone = false;
            c.ChunkId = null;
            c.LastError = ex.Message;
            _spool.SaveChunk(c);
            throw new CloudApiException(503, ex.Message);
        }
    }

    private async Task PostEventsAsync(CancellationToken ct)
    {
        List<CloudEvent> batch;
        lock (_gate) batch = _session.PendingEvents.ToList();
        await _api.PostEventsAsync(_session.OrgId, _session.SubjectId, _session.CloudSessionId!, batch, ct);
        lock (_gate)
        {
            _session.PendingEvents.RemoveAll(e => batch.Any(b => b.ClientEventId == e.ClientEventId));
            _spool.SaveSession(_session);
        }
    }

    private async Task FinalizeAsync(CancellationToken ct)
    {
        var s = _session;
        var finalSeq = s.FinalSeqExclusive ?? s.NextSeq;
        var ended = s.EndedAtEpochMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (finalSeq == 0)
        {
            Log.Warn("The Cloud recording has no data, so it was not finalized.");
            Done();
            return;
        }
        if (s.PendingEvents.Count > 0) await PostEventsAsync(ct);

        var cov = await _api.CoverageAsync(s.OrgId, s.SubjectId, s.CloudSessionId!, ct);
        if (!cov.Finalized)
        {
            await _api.FinalizationIntentAsync(s.OrgId, s.SubjectId, s.CloudSessionId!, s.LocalSessionId, finalSeq, ended, ct);
            var fin = await _api.FinalizeAsync(s.OrgId, s.SubjectId, s.CloudSessionId!, s.LocalSessionId, finalSeq, ended, ct);
            if (!fin.Finalized) throw new CloudApiException(503, $"Cloud has not finalized the session yet ({fin.Status}).");
        }
        var (finalized, seqs) = await _api.ManifestAsync(s.OrgId, s.SubjectId, s.CloudSessionId!, ct);
        if (!finalized) throw new CloudApiException(503, "The manifest does not show the session as finalized yet.");
        var missing = Enumerable.Range(0, finalSeq).Where(i => !seqs.Contains(i)).ToList();
        if (missing.Count > 0) throw new CloudApiException(503, $"The manifest is missing {missing.Count} chunk(s).");

        Log.Info($"Cloud session {s.CloudSessionId} finalized: {finalSeq} chunks, all verified.");
        Done();
    }

    private void Done()
    {
        lock (_gate)
        {
            _session.Finalized = true;
            _state = CloudRecState.Done;
            _waiting = 0;
            _problem = null;
        }
        _spool.SaveSession(_session);
        _spool.DeleteAll();
        Ended?.Invoke(Status);
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            _state = CloudRecState.Error;
            _problem = message;
        }
        Log.Error(message + " The data stays on this PC in " + _spool.Folder);
        Ended?.Invoke(Status);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop; }
            catch { /* already logged */ }
        }
        _cts.Dispose();
    }
}