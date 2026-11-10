using LucidStream.Core.Common;
using LucidStream.Core.Devices;
using LucidStream.Core.Engine;
using LucidStream.Core.Ws;

// Headless Lucid-Stream host: same engine as the Windows app, driven from the command line.
// Useful on lab PCs without a desktop session, in CI, and for building web clients against simulated or replayed data.

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        lucid-stream [source] [options]

        Sources (pick one):
          --simulate               simulated patch (developer)
          --replay <folder>        replay a recorded session folder (developer)
          --loop                   with --replay: start again at the end

        Options:
          --dev                    developer mode: dev origins + bundled Listen page on http://localhost:8766
          --listen                 print a Listen URL (token in the fragment) once streaming starts
          --pair <scopes>          print a 6-digit pairing code, e.g. --pair read:raw,read:processed,write:markers
          --no-lsl                 WebSocket only
          --lan [address]          also accept paired clients on the network (trusted networks only)
          --port <n>               WebSocket port (default 8765)
          --record <folder>        write a session folder there (default: no recording from the CLI)
          --notch <50|60>          line-noise notch (default 60)
          --seconds <n>            stop after n seconds (default: run until Ctrl+C)
          --verbose                print the log

        The real LucidPro Bluetooth connection is in the Windows app (LucidStream.App).
        """);
    return 0;
}

string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null; }

Log.Written += e => { if (args.Contains("--verbose") || e.Level != LogLevel.Info) Console.Error.WriteLine(e); };

var settings = new AppSettings
{
    DeveloperMode = args.Contains("--dev"),
    LslEnabled = !args.Contains("--no-lsl"),
    RecordSession = Arg("--record") != null,
    RecordFolder = Arg("--record") ?? "",
    LanEnabled = args.Contains("--lan"),
    LanAddress = Arg("--lan") ?? "",
    WebSocketPort = int.TryParse(Arg("--port"), out var p) ? p : ProtocolInfo.DefaultPort,
    LineFrequencyHz = double.TryParse(Arg("--notch"), out var n) ? n : 60,
};

IEegSource source;
if (Arg("--replay") is { } folder) source = new ReplaySource(folder, args.Contains("--loop"));
else if (args.Contains("--simulate")) source = new SimulatedSource();
else
{
    Console.Error.WriteLine("Choose a source: --simulate or --replay <folder>. (The Bluetooth patch is in the Windows app.)");
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

await using var engine = new LucidStreamEngine(settings, AppContext.BaseDirectory, "Lucid-Stream CLI 0.1.0");
await engine.ConnectAsync(source);
await engine.StartStreamingAsync();
Console.WriteLine($"Streaming {source.DisplayName}: ws://localhost:{engine.ActivePort}{ProtocolInfo.Path}" + (engine.LslActive ? " + LSL" : ""));
if (engine.DevPageUrl != null) Console.WriteLine($"Developer Listen page: {engine.DevPageUrl}");
if (args.Contains("--listen")) Console.WriteLine($"Listen URL: {engine.CreateListenUrl()}");
if (Arg("--pair") is { } scopes)
{
    var code = engine.CreatePairingCode(scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    Console.WriteLine($"Pairing code: {code.Code} (valid 5 minutes, scopes {string.Join(" ", code.Scopes)})");
}

try
{
    if (int.TryParse(Arg("--seconds"), out var secs)) await Task.Delay(TimeSpan.FromSeconds(secs), stop.Token);
    else await Task.Delay(Timeout.Infinite, stop.Token);
}
catch (OperationCanceledException) { }

await engine.StopStreamingAsync();
return 0;