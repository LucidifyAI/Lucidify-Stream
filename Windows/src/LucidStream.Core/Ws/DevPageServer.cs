using LucidStream.Core.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace LucidStream.Core.Ws;

/// <summary>
/// Developer mode only: serves the bundled reference Listen page from http://localhost:8766 so the whole
/// launch flow can be tested before an approved HTTPS domain hosts the real page. Its origins are in the
/// allowlist's dev_origins, which are honored only in developer mode.
/// </summary>
public sealed class DevPageServer : IAsyncDisposable
{
    private readonly int _port;
    private readonly string _root;
    private WebApplication? _app;

    public DevPageServer(int port, string webRoot)
    {
        _port = port;
        _root = webRoot;
    }

    public string Url => $"http://localhost:{_port}/";

    public async Task StartAsync()
    {
        if (!Directory.Exists(_root))
        {
            Log.Warn($"Developer Listen page not found at {_root}.");
            return;
        }
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(_port));
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            // Same strict policy the hosted Listen page should use: no third-party scripts.
            ctx.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self' ws://localhost:* ws://127.0.0.1:*; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            await next();
        });
        var files = new PhysicalFileProvider(Path.GetFullPath(_root));
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        await app.StartAsync();
        _app = app;
        Log.Info($"Developer Listen page at {Url}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app == null) return;
        using (var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2))) await _app.StopAsync(stopCts.Token);
        await _app.DisposeAsync();
    }
}