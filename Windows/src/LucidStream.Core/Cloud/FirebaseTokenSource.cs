using System.Text.Json.Nodes;

namespace LucidStream.Core.Cloud;

/// <summary>The signed-in Lucidify Cloud user, as read from the Cloud viewer page.</summary>
public sealed record CloudSignIn(string Email, string Uid, string IdToken, long ExpiresAtEpochMs, string RefreshToken, string ApiKey, string OrgApi, string PageOrigin);

/// <summary>
/// Firebase ID tokens for the Cloud API. The Cloud viewer page signs the user in (Google) and hands the session to
/// the app; afterwards the app asks the page for fresh tokens, and if the page cannot answer it refreshes the token
/// itself with Google's secure token endpoint, the same way the Firebase SDK does.
/// </summary>
public sealed class FirebaseTokenSource
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<CancellationToken, Task<CloudSignIn?>>? _askPage;
    private CloudSignIn? _current;

    /// <param name="askPage">Asks the viewer page for the current session (fresh token). May return null.</param>
    public FirebaseTokenSource(Func<CancellationToken, Task<CloudSignIn?>>? askPage) => _askPage = askPage;

    public CloudSignIn? Current => _current;
    public bool SignedIn => _current != null;

    public event Action<CloudSignIn?>? Changed;

    public void Set(CloudSignIn? signIn)
    {
        var changed = signIn?.Uid != _current?.Uid;
        _current = signIn;
        if (changed) Changed?.Invoke(signIn);
    }

    public async Task<string> GetIdTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_current is { } c && c.ExpiresAtEpochMs - now > 120_000) return c.IdToken;

            if (_askPage != null)
            {
                try
                {
                    var fresh = await _askPage(ct);
                    if (fresh != null && fresh.ExpiresAtEpochMs - now > 60_000)
                    {
                        Set(fresh);
                        return fresh.IdToken;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* fall back to refreshing ourselves */ }
            }

            var cur = _current ?? throw new CloudApiException(401, "Not signed in to Lucidify Cloud. Sign in on the Cloud page.");
            var refreshed = await RefreshAsync(cur, ct);
            Set(refreshed);
            return refreshed.IdToken;
        }
        finally { _gate.Release(); }
    }

    private static async Task<CloudSignIn> RefreshAsync(CloudSignIn c, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(c.ApiKey)}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = c.RefreshToken })
        };
        // Browser API keys are often restricted by referrer; present the page's origin like the SDK in the page does.
        req.Headers.Referrer = new Uri(c.PageOrigin.TrimEnd('/') + "/");
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new CloudApiException(resp.StatusCode == System.Net.HttpStatusCode.BadRequest ? 401 : (int)resp.StatusCode, "token refresh failed: " + text);
        var j = JsonNode.Parse(text) as JsonObject ?? throw new CloudApiException(401, "token refresh returned nothing");
        var id = j["id_token"]?.GetValue<string>() ?? throw new CloudApiException(401, "token refresh returned no id_token");
        var refresh = j["refresh_token"]?.GetValue<string>() ?? c.RefreshToken;
        var seconds = long.TryParse(j["expires_in"]?.ToString(), out var s) ? s : 3600;
        return c with { IdToken = id, RefreshToken = refresh, ExpiresAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + seconds * 1000 };
    }
}