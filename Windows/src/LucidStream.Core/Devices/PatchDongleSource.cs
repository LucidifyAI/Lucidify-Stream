using System.Runtime.InteropServices;
using System.Text;
using LucidStream.Core.Common;

namespace LucidStream.Core.Devices;

/// <summary>
/// The LucidPro through CGX's USB dongle, which CGX recommends on PCs: it is permanently paired to one patch and
/// connects to it automatically when the patch is switched on. The dongle is an FTDI serial bridge; this uses FTDI's
/// D2XX driver (ftd2xx.dll, installed with the FTDI driver Windows loads for the dongle), as CGX's own software does.
/// The bytes are the same packets as over Bluetooth and go through the same <see cref="CgxStreamParser"/>.
/// </summary>
public sealed class PatchDongleSource : IEegSource, IRawPacketSource
{
    private const double StallSeconds = 3;

    private readonly PatchBleSettings _settings;
    private readonly CgxStreamParser _parser;
    private IntPtr _handle;
    private string _serial = "";
    private Thread? _reader;
    private volatile bool _stop;

    public PatchDongleSource(PatchBleSettings settings)
    {
        _settings = settings;
        _parser = new CgxStreamParser(settings.PackingMode);
        _parser.Eeg += b => EegReceived?.Invoke(b);
        _parser.Accel += b => AccelReceived?.Invoke(b);
        _parser.Raw += b => RawReceived?.Invoke(b);
        _parser.Status += s =>
        {
            Status = s with { Connected = true, Message = Status.Message };
            StatusChanged?.Invoke(Status);
        };
    }

    public string DisplayName => "LucidPro patch (USB dongle)";
    public DeviceInfo? Info { get; private set; }
    public bool IsReplay => false;
    public ReplayDescriptor? Replay => null;
    public DeviceStatus Status { get; private set; } = new();

    public event Action<EegBlock>? EegReceived;
    public event Action<AccelBlock>? AccelReceived;
    public event Action<DeviceStatus>? StatusChanged;
    public event Action<SourceMarker>? MarkerReceived { add { } remove { } }

    /// <summary>Raw 36-byte packets, unchanged, for Lucidify Cloud upload.</summary>
    public event Action<CgxRawBlock>? RawReceived;

    public CgxStreamParser Parser => _parser;

    /// <summary>A plugged-in CGX dongle (FTDI device whose description contains "CGX"), or null. Never throws.</summary>
    public static DongleInfo? Find()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            if (Ft.FT_CreateDeviceInfoList(out var count) != 0 || count == 0) return null;
            for (uint i = 0; i < count; i++)
            {
                var serial = new byte[16];
                var desc = new byte[64];
                if (Ft.FT_GetDeviceInfoDetail(i, out _, out _, out _, out _, serial, desc, out _) != 0) continue;
                var d = Ascii(desc);
                if (d.Contains("CGX", StringComparison.OrdinalIgnoreCase)) return new DongleInfo(Ascii(serial), d);
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (Exception ex) { Log.Warn($"USB dongle search failed: {ex.Message}"); }
        return null;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var dongle = await Task.Run(Find, ct)
                     ?? throw new InvalidOperationException("No CGX USB dongle found. Plug it in, or connect over Bluetooth.");
        Log.Info($"Opening {dongle.Description} (dongle {dongle.Serial})...");
        _serial = dongle.Serial;
        _handle = Open(_serial, _settings.DongleBaud);

        Info = DeviceInfo.LucidPro($"dongle {dongle.Serial}", "-", CgxStreamParser.AccelRate);
        Status = new DeviceStatus { Connected = false, Message = dongle.Description };
        StatusChanged?.Invoke(Status);
        _stop = false;
        _reader = new Thread(ReadLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "CGX dongle" };
        _reader.Start();
        Log.Info("Dongle open. Switch the patch on if it is off; it connects to the dongle by itself.");
    }

    private static IntPtr Open(string serial, int baud)
    {
        Check(Ft.FT_OpenEx(serial, Ft.OpenBySerialNumber, out var h), "open the dongle (is CGX software using it?)");
        try
        {
            Check(Ft.FT_SetBaudRate(h, (uint)baud), "set the baud rate");
            Check(Ft.FT_SetDataCharacteristics(h, 8, 0, 0), "set 8-N-1");
            Check(Ft.FT_SetFlowControl(h, Ft.FlowRtsCts, 0x11, 0x13), "set RTS/CTS flow control");
            Check(Ft.FT_SetLatencyTimer(h, 2), "set the latency timer");
            Check(Ft.FT_SetTimeouts(h, 100, 100), "set timeouts");
            Check(Ft.FT_Purge(h, Ft.PurgeRx | Ft.PurgeTx), "clear the buffers");
            return h;
        }
        catch
        {
            Ft.FT_Close(h);
            throw;
        }
    }

    /// <summary>
    /// Reads until stopped. The patch reconnects to the dongle by itself when it comes back in range; this marks the
    /// gap and reports it. If the dongle itself fails (unplugged), it is reopened every few seconds until it is back.
    /// </summary>
    private void ReadLoop()
    {
        var buf = new byte[4096];
        var lastData = Clock.Now;
        var live = false;
        double? lostAt = null;
        while (!_stop)
        {
            if (_handle == IntPtr.Zero || Ft.FT_GetQueueStatus(_handle, out var queued) != 0)
            {
                if (_handle != IntPtr.Zero)
                {
                    Log.Warn("USB dongle stopped responding (unplugged?). Reopening it automatically; streaming continues.");
                    Ft.FT_Close(_handle);
                    _handle = IntPtr.Zero;
                    if (live) _parser.NoteLinkLost();
                    live = false;
                    lostAt ??= Clock.Now;
                    SetConnected(false);
                }
                Thread.Sleep(2000);
                try { _handle = Open(_serial, _settings.DongleBaud); Log.Info("USB dongle reopened."); }
                catch (Exception) { /* still unplugged: try again */ }
                continue;
            }
            if (queued == 0)
            {
                if (live && Clock.Now - lastData > StallSeconds)
                {
                    live = false;
                    lostAt = lastData;
                    _parser.NoteLinkLost();
                    SetConnected(false);
                    Log.Warn("No data from the patch for 3 s (out of range or off). It reconnects to the dongle by itself; streaming continues.");
                }
                Thread.Sleep(2);
                continue;
            }
            var want = Math.Min(queued, (uint)buf.Length);
            if (Ft.FT_Read(_handle, buf, want, out var got) != 0 || got == 0) continue;
            var now = Clock.Now;
            lastData = now;
            if (!live)
            {
                live = true;
                SetConnected(true);
                Log.Info(lostAt is double t ? $"Patch is back after {now - t:0} s; streaming resumed." : "Patch data arriving through the dongle.");
                lostAt = null;
            }
            try { _parser.Feed(buf.AsSpan(0, (int)got), now); }
            catch (Exception ex) { Log.Error($"Patch data handling failed: {ex.Message}"); }
        }
    }

    private void SetConnected(bool connected)
    {
        Status = Status with { Connected = connected };
        StatusChanged?.Invoke(Status);
    }

    public Task StopAsync()
    {
        _stop = true;
        _reader?.Join(1000);
        _reader = null;
        if (_handle != IntPtr.Zero)
        {
            Ft.FT_Close(_handle);
            _handle = IntPtr.Zero;
        }
        SetConnected(false);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static void Check(uint status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"Could not {what} (FTDI status {status}).");
    }

    private static string Ascii(byte[] b)
    {
        var n = Array.IndexOf(b, (byte)0);
        return Encoding.ASCII.GetString(b, 0, n < 0 ? b.Length : n).Trim();
    }

    public sealed record DongleInfo(string Serial, string Description);

    /// <summary>The few FTDI D2XX calls needed (see FTDI's D2XX Programmer's Guide).</summary>
    private static class Ft
    {
        private const string Dll = "ftd2xx.dll";
        public const uint OpenBySerialNumber = 1;
        public const ushort FlowRtsCts = 0x0100;
        public const uint PurgeRx = 1, PurgeTx = 2;

        [DllImport(Dll)] public static extern uint FT_CreateDeviceInfoList(out uint numDevs);

        [DllImport(Dll)]
        public static extern uint FT_GetDeviceInfoDetail(uint index, out uint flags, out uint type, out uint id, out uint locId,
            byte[] serialNumber, byte[] description, out IntPtr handle);

        [DllImport(Dll)] public static extern uint FT_OpenEx([MarshalAs(UnmanagedType.LPStr)] string arg, uint flags, out IntPtr handle);
        [DllImport(Dll)] public static extern uint FT_Close(IntPtr handle);
        [DllImport(Dll)] public static extern uint FT_SetBaudRate(IntPtr handle, uint baud);
        [DllImport(Dll)] public static extern uint FT_SetDataCharacteristics(IntPtr handle, byte wordLength, byte stopBits, byte parity);
        [DllImport(Dll)] public static extern uint FT_SetFlowControl(IntPtr handle, ushort flow, byte xon, byte xoff);
        [DllImport(Dll)] public static extern uint FT_SetLatencyTimer(IntPtr handle, byte ms);
        [DllImport(Dll)] public static extern uint FT_SetTimeouts(IntPtr handle, uint readMs, uint writeMs);
        [DllImport(Dll)] public static extern uint FT_Purge(IntPtr handle, uint mask);
        [DllImport(Dll)] public static extern uint FT_GetQueueStatus(IntPtr handle, out uint rxBytes);
        [DllImport(Dll)] public static extern uint FT_Read(IntPtr handle, byte[] buffer, uint toRead, out uint read);
    }
}