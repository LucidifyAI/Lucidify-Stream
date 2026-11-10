using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LucidStream.App.ViewModels;
using LucidStream.Core.Cloud;
using LucidStream.Core.Common;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LucidStream.App.Cloud;

/// <summary>
/// The Cloud viewer page in WebView2. Sign-in happens in the page (Google, as on the website, and remembered in the
/// app's own WebView2 profile). The app reads the page's Firebase session and patient selection with small scripts,
/// and points the page at a new session when a Cloud recording starts.
/// </summary>
public sealed class CloudPageBridge : ICloudPage
{
    private readonly WebView2 _web;
    private readonly Dispatcher _ui;
    private readonly string _url;
    private readonly string _firebaseVersion;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    private CloudPageBridge(WebView2 web, string url, string firebaseVersion)
    {
        _web = web;
        _ui = web.Dispatcher;
        _url = url;
        _firebaseVersion = firebaseVersion;
    }

    public static string ProfileFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LucidStream", "WebView2");

    /// <summary>Create the WebView2 environment (own profile) and open the viewer.</summary>
    public static async Task<CloudPageBridge> CreateAsync(WebView2 web, string url, string firebaseVersion)
    {
        var env = await CoreWebView2Environment.CreateAsync(null, ProfileFolder);
        await web.EnsureCoreWebView2Async(env);
        var bridge = new CloudPageBridge(web, url, firebaseVersion);
        web.CoreWebView2.WebMessageReceived += bridge.OnMessage;
        web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        web.CoreWebView2.Navigate(url);
        return bridge;
    }

    public string PageOrigin => new Uri(_url).GetLeftPart(UriPartial.Authority);

    // ------------------------------------------------------------------ ICloudPage

    public Task<CloudSignIn?> GetSignInAsync(bool forceRefresh, CancellationToken ct) => OnUi<CloudSignIn?>(async () =>
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync(SignInScript(id, forceRefresh));
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
            if (done != tcs.Task) throw new TimeoutException("The Cloud page did not answer (still loading?).");
            var r = tcs.Task.Result;
            if (!r.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Cloud page: " + Str(r, "error"));
            if (!r.TryGetProperty("signedIn", out var si) || !si.GetBoolean()) return null;
            var orgApi = Str(r, "orgApi");
            if (orgApi.Length == 0) throw new InvalidOperationException("The Cloud page did not report its API address (ORG_API).");
            return new CloudSignIn(Str(r, "email"), Str(r, "uid"), Str(r, "idToken"), r.GetProperty("exp").GetInt64(),
                Str(r, "refreshToken"), Str(r, "apiKey"), orgApi, PageOrigin);
        }
        finally { _pending.TryRemove(id, out _); }
    });

    public Task<CloudSelection?> ReadSelectionAsync() => OnUi<CloudSelection?>(async () =>
    {
        var raw = await _web.CoreWebView2.ExecuteScriptAsync(SelectionScript);
        // ExecuteScriptAsync returns the script's value as JSON; the script returns a JSON string.
        var text = JsonSerializer.Deserialize<string>(raw);
        if (string.IsNullOrEmpty(text)) return null;
        var j = JsonDocument.Parse(text).RootElement;
        return new CloudSelection(Str(j, "orgId"), Str(j, "subOrgId"), Str(j, "subjectId"), Str(j, "sessionId"),
            Str(j, "org"), Str(j, "group"), Str(j, "subject"));
    });

    public Task OpenSessionAsync(string orgId, string subOrgId, string subjectId, string sessionId) => OnUi(async () =>
    {
        var state = JsonSerializer.Serialize(new { orgId, subOrgId, subjectId, sessionId });
        await _web.CoreWebView2.ExecuteScriptAsync(
            $"localStorage.setItem('cloudSessionViewerState', {JsonSerializer.Serialize(state)}); location.reload();");
        Log.Info($"Cloud view switched to the new session {sessionId}.");
        return true;
    });

    public void Reload() => _web.CoreWebView2?.Reload();

    // ------------------------------------------------------------------ plumbing

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? text;
        try { text = e.TryGetWebMessageAsString(); }
        catch { return; }
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var j = JsonDocument.Parse(text).RootElement.Clone();
            if (j.TryGetProperty("id", out var id) && id.GetString() is { } key && _pending.TryGetValue(key, out var tcs))
                tcs.TrySetResult(j);
        }
        catch { /* not ours */ }
    }

    private Task<T> OnUi<T>(Func<Task<T>> work)
    {
        if (_ui.CheckAccess()) return work();
        return _ui.InvokeAsync(work).Task.Unwrap();
    }

    private static string Str(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private string SignInScript(string id, bool force) => $$"""
        (async () => {
          const id = "{{id}}";
          const post = m => window.chrome.webview.postMessage(JSON.stringify(Object.assign({ id }, m)));
          try {
            const appMod = await import("https://www.gstatic.com/firebasejs/{{_firebaseVersion}}/firebase-app.js");
            const authMod = await import("https://www.gstatic.com/firebasejs/{{_firebaseVersion}}/firebase-auth.js");
            const cfg = await import("/lucidify-config.js");
            const app = appMod.getApps().length ? appMod.getApp() : appMod.initializeApp(cfg.firebaseConfig);
            const auth = authMod.getAuth(app);
            if (auth.authStateReady) await auth.authStateReady();
            const u = auth.currentUser;
            if (!u) { post({ ok: true, signedIn: false }); return; }
            const r = await u.getIdTokenResult({{(force ? "true" : "false")}});
            post({ ok: true, signedIn: true, email: u.email || "", uid: u.uid, idToken: r.token, exp: Date.parse(r.expirationTime),
                   refreshToken: u.refreshToken || "", apiKey: app.options.apiKey || "", orgApi: String(cfg.ORG_API || "") });
          } catch (e) { post({ ok: false, error: String((e && e.message) || e) }); }
        })();
        """;

    private const string SelectionScript = """
        (function () {
          let s = {};
          try { s = JSON.parse(localStorage.getItem("cloudSessionViewerState") || "{}") || {}; } catch (e) { }
          const t = id => { const el = document.getElementById(id); return el ? el.textContent.trim() : ""; };
          return JSON.stringify({ orgId: s.orgId || "", subOrgId: s.subOrgId || "", subjectId: s.subjectId || "", sessionId: s.sessionId || "",
                                  org: t("orgCrumb"), group: t("subOrgCrumb"), subject: t("subjectCrumb") });
        })()
        """;
}