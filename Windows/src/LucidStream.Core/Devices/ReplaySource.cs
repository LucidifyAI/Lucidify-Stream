using System.Diagnostics;
using LucidStream.Core.Common;
using LucidStream.Core.Recording;

namespace LucidStream.Core.Devices;

/// <summary>
/// Developer replay mode: plays a recorded session folder at real time (1x) through the same stream core as live data.
/// Packets are 10 samples, as from the patch; the recorded sample index is used as the device counter, so recorded
/// gaps reproduce. Recorded markers are re-emitted at their original offsets.
/// </summary>
public sealed class ReplaySource : IEegSource
{
    private const int SamplesPerPacket = 10;
    private readonly SessionReader _reader;
    private readonly bool _loop;
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    public ReplaySource(string sessionFolder, bool loop)
    {
        _reader = new SessionReader(sessionFolder);
        _loop = loop;
        var h = _reader.Header;
        Replay = new ReplayDescriptor(Path.GetFileName(_reader.Folder), h.StartUtc, loop);
    }

    public string DisplayName => $"Replay: {Path.GetFileName(_reader.Folder)}";
    public DeviceInfo? Info { get; private set; }
    public bool IsReplay => true;
    public ReplayDescriptor? Replay { get; }
    public DeviceStatus Status { get; private set; } = new();

    public event Action<EegBlock>? EegReceived;
    public event Action<AccelBlock>? AccelReceived;
    public event Action<DeviceStatus>? StatusChanged;
    public event Action<SourceMarker>? MarkerReceived;

    public Task StartAsync(CancellationToken ct)
    {
        var h = _reader.Header;
        Info = new DeviceInfo(h.Model, h.Serial, h.Firmware, h.EegRate, h.AccelRate, h.EegLabels, h.Reference);
        Status = new DeviceStatus { Connected = true, Contact = h.EegLabels.Select(_ => true).ToArray(), ImpedanceKohm = h.EegLabels.Select(_ => (double?)null).ToArray(), Message = "Replay" };
        StatusChanged?.Invoke(Status);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "Replay" };
        _thread.Start();
        Log.Info($"Replaying {_reader.Folder}{(_loop ? " (loop)" : "")}.");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _thread?.Join(1000);
        Status = Status with { Connected = false };
        StatusChanged?.Invoke(Status);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void Run(CancellationToken ct)
    {
        var h = _reader.Header;
        var ch = h.EegLabels.Length;
        long counterOffset = 0;
        do
        {
            var eeg = _reader.Eeg().GetEnumerator();
            var acc = _reader.Acc().GetEnumerator();
            var markers = _reader.Markers();
            var markerPos = 0;
            var hasAcc = acc.MoveNext();
            double? firstT = null;
            long lastIndex = -1;
            var sw = Stopwatch.StartNew();
            var packet = new List<SessionReader.Row>(SamplesPerPacket);
            var more = eeg.MoveNext();

            while (more && !ct.IsCancellationRequested)
            {
                packet.Clear();
                while (more && packet.Count < SamplesPerPacket)
                {
                    // Start a new packet at an index jump so the gap is seen by the stream core.
                    if (packet.Count > 0 && eeg.Current.Index != packet[^1].Index + 1) break;
                    packet.Add(eeg.Current);
                    more = eeg.MoveNext();
                }
                firstT ??= packet[0].T;
                var offset = packet[^1].T - firstT.Value;
                while (sw.Elapsed.TotalSeconds < offset && !ct.IsCancellationRequested) Thread.Sleep(2);

                var samples = new float[packet.Count * ch];
                for (var i = 0; i < packet.Count; i++) Array.Copy(packet[i].Values, 0, samples, i * ch, ch);
                lastIndex = packet[^1].Index;
                EegReceived?.Invoke(new EegBlock(samples, ch, Clock.Now, counterOffset + packet[0].Index));

                if (hasAcc)
                {
                    var accRows = new List<float>();
                    while (hasAcc && acc.Current.T - firstT.Value <= offset)
                    {
                        accRows.AddRange(acc.Current.Values);
                        hasAcc = acc.MoveNext();
                    }
                    if (accRows.Count > 0) AccelReceived?.Invoke(new AccelBlock(accRows.ToArray(), Clock.Now));
                }

                while (markerPos < markers.Count && markers[markerPos].T - firstT.Value <= offset)
                {
                    MarkerReceived?.Invoke(new SourceMarker(markers[markerPos].Text, Clock.Now));
                    markerPos++;
                }
            }
            counterOffset += lastIndex + 1;
        } while (_loop && !ct.IsCancellationRequested);

        if (!ct.IsCancellationRequested) Log.Info("Replay finished.");
    }
}