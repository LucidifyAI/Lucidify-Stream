using System;
using System.Threading;
using System.Threading.Tasks;
using LucidStream.Core.Common;
using LucidStream.Core.Devices;

namespace LucidStream.App.Ble;

/// <summary>
/// "LucidPro patch" in the app: the CGX USB dongle when one is plugged in (CGX's recommended link on a PC),
/// otherwise Windows Bluetooth. settings.json "ble": { "connection": "auto" | "dongle" | "bluetooth" } overrides.
/// </summary>
public sealed class PatchSource : IEegSource, IRawPacketSource
{
    private readonly PatchBleSettings _settings;
    private IEegSource? _inner;

    public PatchSource(PatchBleSettings settings) => _settings = settings;

    public string DisplayName => _inner?.DisplayName ?? "LucidPro patch";
    public DeviceInfo? Info => _inner?.Info;
    public bool IsReplay => false;
    public ReplayDescriptor? Replay => null;
    public DeviceStatus Status => _inner?.Status ?? new DeviceStatus();

    public event Action<EegBlock>? EegReceived;
    public event Action<AccelBlock>? AccelReceived;
    public event Action<DeviceStatus>? StatusChanged;
    public event Action<SourceMarker>? MarkerReceived { add { } remove { } }

    /// <summary>Raw 36-byte packets, unchanged, for Lucidify Cloud upload.</summary>
    public event Action<CgxRawBlock>? RawReceived;

    public async Task StartAsync(CancellationToken ct)
    {
        var mode = (_settings.Connection ?? "auto").Trim().ToLowerInvariant();
        var useDongle = mode switch
        {
            "dongle" => true,
            "bluetooth" => false,
            _ => PatchDongleSource.Find() != null,
        };

        if (useDongle)
        {
            var d = new PatchDongleSource(_settings);
            d.RawReceived += b => RawReceived?.Invoke(b);
            _inner = d;
        }
        else
        {
            var b = new PatchBleSource(_settings);
            b.RawReceived += r => RawReceived?.Invoke(r);
            _inner = b;
        }
        Log.Info($"Connecting via {(useDongle ? "the CGX USB dongle" : "Bluetooth")}.");
        _inner.EegReceived += e => EegReceived?.Invoke(e);
        _inner.AccelReceived += a => AccelReceived?.Invoke(a);
        _inner.StatusChanged += s => StatusChanged?.Invoke(s);
        try { await _inner.StartAsync(ct); }
        catch
        {
            await _inner.DisposeAsync();
            _inner = null;
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_inner != null) await _inner.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_inner != null) await _inner.DisposeAsync();
        _inner = null;
    }
}