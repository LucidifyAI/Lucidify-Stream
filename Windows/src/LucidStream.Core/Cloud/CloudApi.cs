using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LucidStream.Core.Cloud;

/// <summary>A Lucidify Cloud API failure. Retryable for 408, 409, 425, 429 and 5xx (same rule as the Android app).</summary>
public sealed class CloudApiException : Exception
{
    public CloudApiException(int status, string body)
        : base($"HTTP {status}: {(body.Length > 300 ? body[..300] : body)}")
    {
        Status = status;
        Body = body;
    }

    public int Status { get; }
    public string Body { get; }
    public bool Retryable => Status is 0 or 408 or 409 or 425 or 429 || Status >= 500;
    public bool AuthRequired => Status is 401 or 403;
}

public sealed record CloudSessionInit(string SessionId, int ChunkSeconds, long SessionStartEpochMs, bool Reused);
public sealed record CloudChunkInit(string ChunkId, string? SignedPutUrl, bool UploadRequired, bool AlreadyVerified);
public sealed record CloudChunkComplete(string Status, bool Verified, long? Bytes, string? Sha256);
public sealed record CloudCoverage(long? ContiguousThroughSample, bool Finalized, int MissingRanges);
public sealed record CloudFinalize(string Status, bool Finalized);
public sealed record CloudEvent(string ClientEventId, string Kind, string Label, long SampleIndex, long TOffsetMs, long WallClockEpochMs, long ClientCreatedAtEpochMs);

/// <summary>
/// The Lucidify Cloud org API calls a recording needs, matching the Android app (OrgApi.kt): session init, chunk
/// init / PUT / complete, coverage, finalization intent, finalize, manifest and events. Every call carries the user's
/// Firebase ID token; the token is never sent to the signed Cloud Storage upload URL.
/// </summary>
public sealed class CloudApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly string _base;
    private readonly Func<CancellationToken, Task<string>> _token;

    public CloudApi(string apiBase, Func<CancellationToken, Task<string>> idToken)
    {
        _base = apiBase.TrimEnd('/');
        _token = idToken;
    }

    private string SessionUrl(string orgId, string subjectId) =>
        $"{_base}/v1/org/{Uri.EscapeDataString(orgId)}/subjects/{Uri.EscapeDataString(subjectId)}/session";

    public async Task<CloudSessionInit> SessionInitAsync(string orgId, string subjectId, string clientSessionId, long startedAtEpochMs,
        int sampleRateHz, IReadOnlyList<string> channels, int chunkSeconds, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["clientSessionId"] = clientSessionId,
            ["startedAtEpochMs"] = startedAtEpochMs,
            ["format"] = "cgx",
            ["formatVersion"] = 1,
            ["sampleRateHz"] = sampleRateHz,
            ["channels"] = new JsonArray(channels.Select(c => (JsonNode)c!).ToArray()),
            ["chunkSeconds"] = chunkSeconds,
        };
        var j = await PostAsync($"{SessionUrl(orgId, subjectId)}/init", body, $"session:{clientSessionId}:init", ct);
        return new CloudSessionInit(Str(j, "sessionId") ?? throw new CloudApiException(200, "session init returned no sessionId"),
            Int(j, "chunkSeconds") ?? chunkSeconds, Long(j, "sessionStartEpochMs") ?? startedAtEpochMs, Bool(j, "reused") ?? false);
    }

    public async Task<CloudChunkInit> ChunkInitAsync(string orgId, string subjectId, string sessionId, string clientChunkKey, int seq,
        long firstSampleIndex, int sampleCount, long tStartMs, long tEndMs, long bytes, string sha256b64, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["clientChunkKey"] = clientChunkKey,
            ["seq"] = seq,
            ["firstSampleIndex"] = firstSampleIndex,
            ["sampleCount"] = sampleCount,
            ["tStartMs"] = tStartMs,
            ["tEndMs"] = tEndMs,
            ["bytes"] = bytes,
            ["contentType"] = "application/octet-stream",
            ["sha256"] = sha256b64,
        };
        var j = await PostAsync($"{SessionUrl(orgId, subjectId)}/{Uri.EscapeDataString(sessionId)}/chunk/init", body, $"chunk:{clientChunkKey}:init", ct);
        return new CloudChunkInit(Str(j, "chunkId") ?? throw new CloudApiException(200, "chunk init returned no chunkId"),
            Str(j, "signedPutUrl"), Bool(j, "uploadRequired") ?? true, Bool(j, "alreadyVerified") ?? false);
    }

    /// <summary>Upload the chunk bytes to the signed Cloud Storage URL. No Authorization header.</summary>
    public static async Task PutSignedAsync(string signedPutUrl, byte[] payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, signedPutUrl) { Content = new ByteArrayContent(payload) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await Send(req, ct);
        if (!resp.IsSuccessStatusCode) throw new CloudApiException((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    public async Task<CloudChunkComplete> ChunkCompleteAsync(string orgId, string subjectId, string sessionId, string chunkId,
        string clientChunkKey, long bytes, string sha256b64, CancellationToken ct)
    {
        var body = new JsonObject { ["clientChunkKey"] = clientChunkKey, ["bytes"] = bytes, ["sha256"] = sha256b64 };
        var j = await PostAsync($"{SessionUrl(orgId, subjectId)}/{Uri.EscapeDataString(sessionId)}/chunk/{Uri.EscapeDataString(chunkId)}/complete",
            body, $"chunk:{clientChunkKey}:complete", ct);
        var status = Str(j, "status") ?? "";
        return new CloudChunkComplete(status, Bool(j, "verified") ?? status.Equals("verified", StringComparison.OrdinalIgnoreCase),
            Long(j, "bytes"), Str(j, "sha256"));
    }

    public async Task<CloudCoverage> CoverageAsync(string orgId, string subjectId, string sessionId, CancellationToken ct)
    {
        var j = await GetAsync($"{SessionUrl(orgId, subjectId)}/{Uri.EscapeDataString(sessionId)}/coverage", ct);
        var missing = j["missingRanges"] is JsonArray a ? a.Count : 0;
        return new CloudCoverage(Long(j, "contiguousThroughSample"), Bool(j, "finalized") ?? false, missing);
    }

    public Task FinalizationIntentAsync(string orgId, string subjectId, string sessionId, string clientSessionId, int finalSeqExclusive,
        long endedAtEpochMs, CancellationToken ct) =>
        PostAsync($"{SessionUrl(orgId, subjectId)}/{Uri.EscapeDataString(sessionId)}/finalization-intent",
            FinalBody(clientSessionId, finalSeqExclusive, endedAtEpochMs), $"session:{clientSessionId}:finalization-intent", ct);

    public async Task<CloudFinalize> FinalizeAsync(string orgId, string subjectId, string sessionId, string clientSessionId,
        int finalSeqExclusive, long endedAtEpochMs, CancellationToken ct)
    {
        var j = await PostAsync($"{SessionUrl(orgId, subjectId)}/{Uri.EscapeDataString(sessionId)}/finalize",
            FinalBody(clientSessionId, finalSeqExclusive, endedAtEpochMs), $"session:{clientSessionId}:finalize", ct);
        var status = Str(j, "status") ?? "";
        return new CloudFinalize(status, Bool(j, "finalized") ?? status.Equals("finalized", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Chunk sequence numbers in the manifest (urls=0, as the Android app asks).</summary>
    public async Task<(bool Finalized, HashSet<int> Seqs)> ManifestAsync(string orgId, string subjectId, string sessionId, CancellationToken ct)
    {
        var j = await GetAsync($"{SessionUrl(orgId, subjectId)}/{Uri.EscapeDataString(sessionId)}/manifest?urls=0", ct);
        var seqs = new HashSet<int>();
        if (j["chunks"] is JsonArray chunks)
            foreach (var c in chunks.OfType<JsonObject>())
                if (Int(c, "seq") is int s) seqs.Add(s);
        return (Bool(j, "finalized") ?? false, seqs);
    }

    /// <summary>Post markers. Returns how many the server accepted (created or already existing).</summary>
    public async Task<int> PostEventsAsync(string orgId, string subjectId, string sessionId, IReadOnlyList<CloudEvent> events, CancellationToken ct)
    {
        var arr = new JsonArray();
        foreach (var e in events)
            arr.Add(new JsonObject
            {
                ["clientEventId"] = e.ClientEventId,
                ["kind"] = e.Kind,
                ["label"] = e.Label,
                ["sampleIndex"] = e.SampleIndex,
                ["tOffsetMs"] = e.TOffsetMs,
                ["wallClockEpochMs"] = e.WallClockEpochMs,
                ["clientCreatedAtEpochMs"] = e.ClientCreatedAtEpochMs,
            });
        var j = await PostAsync($"{SessionUrl(orgId, subjectId)}/{Uri.EscapeDataString(sessionId)}/events", new JsonObject { ["events"] = arr }, null, ct);
        return j["results"] is JsonArray r
            ? r.OfType<JsonObject>().Count(x => Str(x, "status") is "created" or "existing")
            : events.Count;
    }

    // ------------------------------------------------------------------ plumbing

    private static JsonObject FinalBody(string clientSessionId, int finalSeqExclusive, long endedAtEpochMs) => new()
    {
        ["clientSessionId"] = clientSessionId,
        ["finalSeqExclusive"] = finalSeqExclusive,
        ["endedAtEpochMs"] = endedAtEpochMs,
    };

    private async Task<JsonObject> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAuthorized(req, ct);
    }

    private async Task<JsonObject> PostAsync(string url, JsonObject body, string? idempotencyKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (idempotencyKey != null)
        {
            req.Headers.Add("Idempotency-Key", idempotencyKey);
            req.Headers.Add("X-Client-Operation-Id", idempotencyKey);
        }
        return await SendAuthorized(req, ct);
    }

    private async Task<JsonObject> SendAuthorized(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct));
        using var resp = await Send(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new CloudApiException((int)resp.StatusCode, text);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    private static async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
    {
        try { return await Http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new CloudApiException(0, ex.Message); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new CloudApiException(0, "timed out: " + ex.Message); }
    }

    private static string? Str(JsonObject j, string name) =>
        j[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static long? Long(JsonObject j, string name) => j[name] is JsonValue v
        ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) ? p : null
        : null;

    private static int? Int(JsonObject j, string name) => Long(j, name) is long l ? (int)l : null;

    private static bool? Bool(JsonObject j, string name) => j[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}