using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LucidStream.Core.Common;
using LucidStream.Core.Devices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace LucidStream.App.Ble;

/// <summary>
/// The LucidPro (CGX Patch EEG) over Windows' built-in Bluetooth LE, matching the Lucidify Android app:
/// find the patch by the CGX service UUID, subscribe to both notify characteristics (...D703 and ...D704), and treat
/// all notified bytes as one stream. The patch needs no start command. Parsing is in Core (CgxStreamParser).
/// </summary>
public sealed class PatchBleSource : IEegSource, IRawPacketSource
{
    private readonly PatchBleSettings _settings;
    private readonly CgxStreamParser _parser;
    private readonly List<GattCharacteristic> _chars = new();
    private readonly Dictionary<Guid, long> _bytesPerChar = new();
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _service;
    private string _name = "";
    private bool _byteSplitLogged;
    private double _connectedAt;

    public PatchBleSource(PatchBleSettings settings)
    {
        _settings = settings;
        _parser = new CgxStreamParser(settings.PackingMode);
        _parser.Eeg += b => EegReceived?.Invoke(b);
        _parser.Accel += b => AccelReceived?.Invoke(b);
        _parser.Raw += b => RawReceived?.Invoke(b);
        _parser.Status += s =>
        {
            Status = s with { Connected = _device?.ConnectionStatus == BluetoothConnectionStatus.Connected, Message = _name };
            StatusChanged?.Invoke(Status);
        };
    }

    public string DisplayName => "LucidPro patch (Bluetooth)";
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

    public async Task StartAsync(CancellationToken ct)
    {
        var (address, name) = await ScanAsync(ct);
        _name = name;
        Log.Info($"Found {name}; connecting...");

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                  ?? throw new InvalidOperationException($"Could not open {name}.");
        _device.ConnectionStatusChanged += OnConnectionChanged;
        try
        {
            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            _session.MaintainConnection = true;
        }
        catch (Exception ex) { Log.Warn($"Could not ask Windows to keep the link open: {ex.Message}"); }

        var services = await _device.GetGattServicesForUuidAsync(_settings.ServiceGuid, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new InvalidOperationException($"{name}: CGX data service not found ({services.Status}). Is the patch connected to another app or the USB dongle?");
        _service = services.Services[0];

        foreach (var uuid in _settings.DataCharacteristicGuids)
        {
            var found = await _service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
            if (found.Status != GattCommunicationStatus.Success || found.Characteristics.Count == 0)
            {
                Log.Warn($"{name}: characteristic {uuid} not found ({found.Status}).");
                continue;
            }
            var c = found.Characteristics[0];
            c.ValueChanged += OnValueChanged;
            _chars.Add(c);
        }
        if (_chars.Count == 0) throw new InvalidOperationException($"{name}: no CGX data characteristics found.");
        await SubscribeAsync();

        var serial = name.Length > 0 ? name : address.ToString("X12", CultureInfo.InvariantCulture);
        Info = DeviceInfo.LucidPro(serial, "-", CgxStreamParser.AccelRate);
        _connectedAt = Clock.Now;
        Status = new DeviceStatus { Connected = true, Message = name };
        StatusChanged?.Invoke(Status);
        Log.Info($"{name} connected{(_session != null ? $" (max packet {_session.MaxPduSize} bytes)" : "")}; waiting for data.");
    }

    private async Task SubscribeAsync()
    {
        foreach (var c in _chars)
        {
            var r = await c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (r != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"{_name}: could not subscribe to {c.Uuid} ({r}).");
        }
    }

    private async Task<(ulong Address, string Name)> ScanAsync(CancellationToken ct)
    {
        var service = _settings.ServiceGuid;
        var found = new TaskCompletionSource<(ulong, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var names = new Dictionary<ulong, string>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            var localName = e.Advertisement.LocalName;
            lock (names)
            {
                if (!string.IsNullOrEmpty(localName)) names[e.BluetoothAddress] = localName;
                names.TryGetValue(e.BluetoothAddress, out var known);
                var byService = e.Advertisement.ServiceUuids.Contains(service);
                var byName = !string.IsNullOrEmpty(known) && known.StartsWith(_settings.NamePrefix, StringComparison.OrdinalIgnoreCase);
                if (byService || byName) found.TrySetResult((e.BluetoothAddress, known ?? ""));
            }
        };
        Log.Info($"Scanning for a LucidPro patch (up to {_settings.ScanSeconds} s). Switch the patch on.");
        watcher.Start();
        try
        {
            var done = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(_settings.ScanSeconds), ct));
            if (done != found.Task)
                throw new TimeoutException("No LucidPro patch found. Check it is on and charged, and not connected to the Android app or the CGX USB dongle.");
            var (address, firstName) = await found.Task;
            // Give the scan response a moment to deliver the name if the first hit was the bare advertisement.
            if (firstName.Length == 0) await Task.Delay(500, ct);
            string? latest;
            lock (names) names.TryGetValue(address, out latest);
            return (address, !string.IsNullOrEmpty(latest) ? latest : firstName.Length > 0 ? firstName : "CGX Patch");
        }
        finally { watcher.Stop(); }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var receivedAt = Clock.Now;
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        try
        {
            CountBytes(sender.Uuid, bytes.Length, receivedAt);
            _parser.Feed(bytes, receivedAt);
        }
        catch (Exception ex) { Log.Error($"Patch data handling failed: {ex.Message}"); }
    }

    /// <summary>Logs once which characteristic carries the data, for the record (the Android app subscribes to both).</summary>
    private void CountBytes(Guid uuid, int n, double now)
    {
        lock (_bytesPerChar)
        {
            _bytesPerChar[uuid] = (_bytesPerChar.TryGetValue(uuid, out var v) ? v : 0) + n;
            if (_byteSplitLogged || now - _connectedAt < 3) return;
            _byteSplitLogged = true;
            var parts = new List<string>();
            foreach (var kv in _bytesPerChar) parts.Add($"...{kv.Key.ToString()[^4..]}: {kv.Value:N0} bytes");
            Log.Info("Bluetooth data so far by characteristic: " + string.Join(", ", parts) + ".");
        }
    }

    private async void OnConnectionChanged(BluetoothLEDevice sender, object args)
    {
        var connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        Status = Status with { Connected = connected };
        StatusChanged?.Invoke(Status);
        if (!connected)
        {
            _parser.NoteLinkLost();
            Log.Warn("Patch disconnected; reconnecting when it is back in range.");
            return;
        }
        Log.Info("Patch reconnected; resubscribing.");
        try { await SubscribeAsync(); }
        catch (Exception ex) { Log.Warn($"Resubscribe failed: {ex.Message}"); }
    }

    public async Task StopAsync()
    {
        foreach (var c in _chars)
        {
            c.ValueChanged -= OnValueChanged;
            try { await c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); }
            catch (Exception ex) { Log.Warn($"Bluetooth stop: {ex.Message}"); }
        }
        _chars.Clear();
        if (_device != null) _device.ConnectionStatusChanged -= OnConnectionChanged;
        _service?.Dispose();
        _session?.Dispose();
        _device?.Dispose();
        _service = null;
        _session = null;
        _device = null;
        Status = Status with { Connected = false };
        StatusChanged?.Invoke(Status);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}