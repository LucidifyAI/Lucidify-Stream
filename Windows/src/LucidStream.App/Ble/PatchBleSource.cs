using System;
using System.Globalization;
using System.Linq;
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
/// The LucidPro (CGX) patch over Windows' built-in Bluetooth LE stack.
///
/// Scanning, connecting, subscribing to the data characteristic and reconnect reporting are implemented.
/// Turning notification bytes into EEG needs the CGX packet format (see Core/Devices/CgxPacketParser.cs)
/// and the service/characteristic UUIDs (PatchBleSettings, stored in settings.json under "ble").
/// Until both are filled in, Connect explains what is missing.
///
/// If the patch only streams through CGX's USB dongle (a serial port), add a sibling source that reads the
/// COM port and feeds the same CgxPacketParser; nothing downstream changes.
/// </summary>
public sealed class PatchBleSource : IEegSource
{
    private readonly PatchBleSettings _settings;
    private readonly IPatchPacketParser _parser = new CgxPacketParser();
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _data;

    public PatchBleSource(PatchBleSettings settings) => _settings = settings;

    public string DisplayName => "LucidPro patch (Bluetooth)";
    public DeviceInfo? Info { get; private set; }
    public bool IsReplay => false;
    public ReplayDescriptor? Replay => null;
    public DeviceStatus Status { get; private set; } = new();

    public event Action<EegBlock>? EegReceived;
    public event Action<AccelBlock>? AccelReceived;
    public event Action<DeviceStatus>? StatusChanged;
    public event Action<SourceMarker>? MarkerReceived { add { } remove { } }

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_settings.IsConfigured || !CgxPacketParser.IsImplemented)
        {
            throw new InvalidOperationException(
                "The LucidPro Bluetooth protocol is not configured in this build yet. It needs the CGX packet format " +
                "(CgxPacketParser.cs) and the service/characteristic UUIDs (settings.json, \"ble\"). " +
                "Use Developer mode > Simulated patch or Replay meanwhile.");
        }

        var (address, name) = await ScanAsync(ct);
        Log.Info($"Found {name}; connecting...");

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                  ?? throw new InvalidOperationException($"Could not open {name}. Is it paired with another app?");
        _device.ConnectionStatusChanged += OnConnectionChanged;

        var services = await _device.GetGattServicesForUuidAsync(Guid.Parse(_settings.DataServiceUuid), BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new InvalidOperationException($"{name}: EEG service not found ({services.Status}).");
        _service = services.Services[0];

        var chars = await _service.GetCharacteristicsForUuidAsync(Guid.Parse(_settings.DataCharacteristicUuid), BluetoothCacheMode.Uncached);
        if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
            throw new InvalidOperationException($"{name}: EEG data characteristic not found ({chars.Status}).");
        _data = chars.Characteristics[0];
        _data.ValueChanged += OnValueChanged;

        var cccd = await _data.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (cccd != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"{name}: could not subscribe to EEG notifications ({cccd}).");

        if (_settings.StartCommandHex.Length > 0 && _settings.CommandCharacteristicUuid.Length > 0)
        {
            var cmdChars = await _service.GetCharacteristicsForUuidAsync(Guid.Parse(_settings.CommandCharacteristicUuid), BluetoothCacheMode.Uncached);
            if (cmdChars.Status == GattCommunicationStatus.Success && cmdChars.Characteristics.Count > 0)
            {
                var writer = new DataWriter();
                writer.WriteBytes(Convert.FromHexString(_settings.StartCommandHex));
                await cmdChars.Characteristics[0].WriteValueAsync(writer.DetachBuffer());
            }
        }

        var serial = name.Length > _settings.NamePrefix.Length ? name : address.ToString("X12", CultureInfo.InvariantCulture);
        Info = DeviceInfo.LucidPro(serial, "unknown", _settings.AccelRateHz);
        Status = new DeviceStatus { Connected = true, Contact = new[] { true, true }, Message = name };
        StatusChanged?.Invoke(Status);
        Log.Info($"{name} streaming.");
    }

    private async Task<(ulong Address, string Name)> ScanAsync(CancellationToken ct)
    {
        var found = new TaskCompletionSource<(ulong, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            var localName = e.Advertisement.LocalName;
            if (!string.IsNullOrEmpty(localName) && localName.StartsWith(_settings.NamePrefix, StringComparison.OrdinalIgnoreCase))
                found.TrySetResult((e.BluetoothAddress, localName));
        };
        Log.Info($"Scanning for a patch named '{_settings.NamePrefix}...' (up to {_settings.ScanSeconds} s). Turn the patch on.");
        watcher.Start();
        try
        {
            var done = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(_settings.ScanSeconds), ct));
            if (done != found.Task) throw new TimeoutException("No LucidPro patch found. Check it is on, charged, and not connected to another app.");
            return await found.Task;
        }
        finally { watcher.Stop(); }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var receivedAt = Clock.Now;
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        if (!_parser.TryParse(bytes, receivedAt, out var eeg, out var accel, out var status)) return;
        if (eeg != null) EegReceived?.Invoke(eeg);
        if (accel != null) AccelReceived?.Invoke(accel);
        if (status != null)
        {
            Status = status with { Connected = true };
            StatusChanged?.Invoke(Status);
        }
    }

    private void OnConnectionChanged(BluetoothLEDevice sender, object args)
    {
        var connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        Status = Status with { Connected = connected };
        StatusChanged?.Invoke(Status);
        if (connected) Log.Info("Patch reconnected.");
        else Log.Warn("Patch disconnected; Windows will reconnect when it is back in range.");
    }

    public async Task StopAsync()
    {
        try
        {
            if (_data != null)
            {
                _data.ValueChanged -= OnValueChanged;
                await _data.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
            }
        }
        catch (Exception ex) { Log.Warn($"Bluetooth stop: {ex.Message}"); }
        if (_device != null) _device.ConnectionStatusChanged -= OnConnectionChanged;
        _service?.Dispose();
        _device?.Dispose();
        _data = null;
        _service = null;
        _device = null;
        Status = Status with { Connected = false };
        StatusChanged?.Invoke(Status);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}