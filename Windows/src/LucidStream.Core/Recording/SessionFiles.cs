using System.Globalization;
using System.Text;
using System.Text.Json;
using LucidStream.Core.Common;
using LucidStream.Core.Devices;
using LucidStream.Core.Streams;

namespace LucidStream.Core.Recording;

/// <summary>session.json in a Lucid-Stream session folder.</summary>
public sealed class SessionHeader
{
    public string Format { get; set; } = "lucid-stream-session";
    public int FormatVersion { get; set; } = 1;
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Firmware { get; set; } = "";
    public double EegRate { get; set; }
    public double AccelRate { get; set; }
    public string[] EegLabels { get; set; } = Array.Empty<string>();
    public string Reference { get; set; } = "";
    public string EegUnit { get; set; } = "microvolts";
    public DateTimeOffset StartUtc { get; set; }
    public double StartLslTime { get; set; }
    public string App { get; set; } = "";
    public bool Replay { get; set; }
    public string Notes { get; set; } = "Raw EEG exactly as streamed (eeg.raw). NaN = gap-filled sample. Timestamps on the LSL clock of the recording PC.";
}

/// <summary>
/// The optional local session file (spec: "Local session file"). A folder with session.json plus CSV files:
/// eeg_raw.csv (index, t, AFz, Fp2), acc.csv (index, t, x, y, z), markers.csv (index, t, text), gaps.csv.
/// Values are written with round-trip precision so a replay reproduces eeg.raw exactly.
///
/// TODO(format): switch to the Lucidify session format used by the Android/Windows apps once it is shared,
/// so recordings flow into Lucidify Cloud tools; this CSV layout is a stand-in.
/// </summary>
public sealed class SessionWriter : IStreamSink, IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly object _gate = new();
    private readonly StreamWriter _eeg, _acc, _markers, _gaps;
    private bool _disposed;
    private DateTime _lastFlush = DateTime.UtcNow;

    public string Folder { get; }

    public SessionWriter(string parentFolder, DeviceInfo device, bool replay, string appName)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);
        Folder = Path.Combine(parentFolder, $"{stamp}_{device.Serial}{(replay ? "_replay" : "")}");
        Directory.CreateDirectory(Folder);
        var header = new SessionHeader
        {
            Model = device.Model, Serial = device.Serial, Firmware = device.Firmware, EegRate = device.EegRate,
            AccelRate = device.AccelRate, EegLabels = device.EegLabels, Reference = device.Reference,
            StartUtc = DateTimeOffset.UtcNow, StartLslTime = Clock.Now, App = appName, Replay = replay
        };
        File.WriteAllText(Path.Combine(Folder, "session.json"), JsonSerializer.Serialize(header, SessionJson.Options));
        _eeg = Open("eeg_raw.csv", "index,t," + string.Join(",", device.EegLabels));
        _acc = Open("acc.csv", "index,t,x,y,z");
        _markers = Open("markers.csv", "index,t,text");
        _gaps = Open("gaps.csv", "stream,from_index,count,reason,filled");
        Log.Info($"Recording to {Folder}");
    }

    private StreamWriter Open(string name, string header)
    {
        var w = new StreamWriter(Path.Combine(Folder, name), false, new UTF8Encoding(false), 1 << 16);
        w.WriteLine(header);
        return w;
    }

    public void OnFrame(StreamFrame f)
    {
        lock (_gate)
        {
            if (_disposed) return;
            switch (f.Def.Key)
            {
                case StreamKeys.EegRaw: WriteNumeric(_eeg, f); break;
                case StreamKeys.Acc: WriteNumeric(_acc, f); break;
                case StreamKeys.Markers:
                    _markers.WriteLine($"{f.Index},{f.T0.ToString("R", Inv)},{Csv(f.Text ?? "")}");
                    _markers.Flush();
                    break;
            }
            if ((DateTime.UtcNow - _lastFlush).TotalSeconds > 2)
            {
                _eeg.Flush(); _acc.Flush();
                _lastFlush = DateTime.UtcNow;
            }
        }
    }

    public void OnGap(GapNotice g)
    {
        lock (_gate)
        {
            if (_disposed || g.StreamKey != StreamKeys.EegRaw) return;
            _gaps.WriteLine($"{g.StreamKey},{g.FromIndex},{g.Count},{g.Reason},{(g.Filled ? 1 : 0)}");
            _gaps.Flush();
        }
    }

    private static void WriteNumeric(StreamWriter w, StreamFrame f)
    {
        var ch = f.Def.ChannelCount;
        var sb = new StringBuilder(64);
        for (var i = 0; i < f.SampleCount; i++)
        {
            sb.Clear();
            sb.Append(f.Index + i).Append(',').Append(f.Timestamps[i].ToString("R", Inv));
            for (var c = 0; c < ch; c++) sb.Append(',').Append(f.Data![i * ch + c].ToString("R", Inv));
            w.WriteLine(sb.ToString());
        }
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _eeg.Dispose(); _acc.Dispose(); _markers.Dispose(); _gaps.Dispose();
        }
        Log.Info("Recording closed.");
    }
}

public static class SessionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>Reads a session folder back for replay.</summary>
public sealed class SessionReader
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public SessionHeader Header { get; }
    public string Folder { get; }

    public SessionReader(string folderOrFile)
    {
        Folder = File.Exists(folderOrFile) ? Path.GetDirectoryName(Path.GetFullPath(folderOrFile))! : Path.GetFullPath(folderOrFile);
        var headerPath = Path.Combine(Folder, "session.json");
        if (!File.Exists(headerPath)) throw new FileNotFoundException("Not a Lucid-Stream session folder (session.json missing).", headerPath);
        Header = JsonSerializer.Deserialize<SessionHeader>(File.ReadAllText(headerPath), SessionJson.Options)
                 ?? throw new InvalidDataException("session.json is empty");
    }

    public sealed record Row(long Index, double T, float[] Values);
    public sealed record MarkerRow(long Index, double T, string Text);

    public IEnumerable<Row> Eeg() => Numeric("eeg_raw.csv");
    public IEnumerable<Row> Acc() => Numeric("acc.csv");

    public List<MarkerRow> Markers()
    {
        var path = Path.Combine(Folder, "markers.csv");
        var list = new List<MarkerRow>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var p = line.Split(',', 3);
            if (p.Length < 3) continue;
            var text = p[2];
            if (text.StartsWith('"') && text.EndsWith('"')) text = text[1..^1].Replace("\"\"", "\"");
            list.Add(new MarkerRow(long.Parse(p[0], Inv), double.Parse(p[1], Inv), text));
        }
        return list;
    }

    private IEnumerable<Row> Numeric(string file)
    {
        var path = Path.Combine(Folder, file);
        if (!File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (line.Length == 0) continue;
            var p = line.Split(',');
            var values = new float[p.Length - 2];
            for (var i = 0; i < values.Length; i++) values[i] = float.Parse(p[i + 2], NumberStyles.Float, Inv);
            yield return new Row(long.Parse(p[0], Inv), double.Parse(p[1], Inv), values);
        }
    }
}