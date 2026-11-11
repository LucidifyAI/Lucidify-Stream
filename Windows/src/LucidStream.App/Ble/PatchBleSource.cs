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
/// If the data stops (out of range, patch off) it reconnects by itself until the patch is back.
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

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);
    private const double StallSeconds = 3;
    private ulong _address;
    private double _lastDataAt;
    private volatile bool _linkUp;
    private CancellationTokenSource? _life;
    private Task? _supervisor;

    public PatchBleSource(PatchBleSettings settings)
    {
        _settings = settings;
        _parser = new CgxStreamParser(settings.PackingMode);
        _parser.Eeg += b => EegReceived?.Invoke(b);
        _parser.Accel += b => AccelReceived?.Invoke(b);
        _parser.Raw += b => RawReceived?.Invoke(b);
        _parser.Status += s =>
        {
            Status = s with { Connected = _linkUp, Message = Status.Message };
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
        var (address, name) = await ScanAsync(ct, _settings.ScanSeconds);
        _address = address;
        _name = name;
        Log.Info($"Found {name}; connecting...");
        _connectedAt = Clock.Now;
        await ConnectAsync(ct);

        var serial = name.Length > 0 ? name : address.ToString("X12", CultureInfo.InvariantCulture);
        Info = DeviceInfo.LucidPro(serial, "-", CgxStreamParser.AccelRate);
        _connectedAt = Clock.Now;
        SetConnected(true, name);
        Log.Info($"{name} connected{(_session != null ? $" (max packet {_session.MaxPduSize} bytes)" : "")}; waiting for data.");

        _life = new CancellationTokenSource();
        var life = _life.Token;
        _supervisor = Task.Run(() => SuperviseAsync(life));
    }

    /// <summary>Open the patch at the known address, find the CGX characteristics and subscribe. Cleans up on failure.</summary>
    private async Task ConnectAsync(CancellationToken ct)
    {
        try
        {
            _device = await WithTimeout(BluetoothLEDevice.FromBluetoothAddressAsync(_address).AsTask(), ConnectTimeout, ct)
                      ?? throw new InvalidOperationException($"Could not open {_name}.");
            _device.ConnectionStatusChanged += OnConnectionChanged;
            try
            {
                _session = await WithTimeout(GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(), ConnectTimeout, ct);
                _session.MaintainConnection = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Could not ask Windows to keep the link open: {ex.Message}"); }

            var services = await WithTimeout(_device.GetGattServicesForUuidAsync(_settings.ServiceGuid, BluetoothCacheMode.Uncached).AsTask(), ConnectTimeout, ct);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new InvalidOperationException($"{_name}: CGX data service not found ({services.Status}). Is the patch connected to another app or the USB dongle?");
            _service = services.Services[0];

            foreach (var uuid in _settings.DataCharacteristicGuids)
            {
                var found = await WithTimeout(_service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached).AsTask(), ConnectTimeout, ct);
                if (found.Status != GattCommunicationStatus.Success || found.Characteristics.Count == 0)
                {
                    Log.Warn($"{_name}: characteristic {uuid} not found ({found.Status}).");
                    continue;
                }
                var c = found.Characteristics[0];
                c.ValueChanged += OnValueChanged;
                _chars.Add(c);
            }
            if (_chars.Count == 0) throw new InvalidOperationException($"{_name}: no CGX data characteristics found.");

            foreach (var c in _chars)
            {
                var r = await WithTimeout(c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(), ConnectTimeout, ct);
                if (r != GattCommunicationStatus.Success)
                    throw new InvalidOperationException($"{_name}: could not subscribe to {c.Uuid} ({r}).");
            }
            Interlocked.Exchange(ref _lastDataAt, Clock.Now);
        }
        catch
        {
            TearDown();
            throw;
        }
    }

    /// <summary>
    /// Watches the data. When the patch goes quiet (out of range, switched off, Windows dropped the link) it reconnects
    /// on its own and keeps trying until the patch is back or the user disconnects; streaming carries on throughout and
    /// the missing stretch is marked as a gap.
    /// </summary>
    private async Task SuperviseAsync(CancellationToken life)
    {
        try
        {
            while (!life.IsCancellationRequested)
            {
                await Task.Delay(1000, life);
                var silent = Clock.Now - Interlocked.CompareExchange(ref _lastDataAt, 0, 0);
                if (silent < StallSeconds) continue;

                var lostAt = Clock.Now;
                _parser.NoteLinkLost();
                SetConnected(false, $"{_name}: out of range or off, reconnecting...");
                Log.Warn($"No data from {_name} for {silent:0} s (out of range or switched off). Reconnecting automatically; streaming continues.");

                for (var attempt = 1; !life.IsCancellationRequested; attempt++)
                {
                    TearDown();
                    try
                    {
                        // Every few tries, look for the patch again in case it now advertises under a new address.
                        if (attempt % 5 == 0)
                        {
                            var (address, _) = await ScanAsync(life, 6, quiet: true);
                            _address = address;
                        }
                        var tryStarted = Clock.Now;
                        await ConnectAsync(life);
                        // Connected: wait for the first data to call it recovered.
                        while (Interlocked.CompareExchange(ref _lastDataAt, 0, 0) <= tryStarted + 0.0001 && Clock.Now - tryStarted < 6)
                            await Task.Delay(200, life);
                        if (Clock.Now - tryStarted >= 6 && Interlocked.CompareExchange(ref _lastDataAt, 0, 0) <= tryStarted + 0.0001)
                            throw new TimeoutException("connected but no data yet");
                        SetConnected(true, _name);
                        Log.Info($"{_name} is back after {Clock.Now - lostAt:0} s; streaming resumed.");
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (attempt == 1 || attempt % 10 == 0)
                            Log.Info($"Still reconnecting to {_name} ({Clock.Now - lostAt:0} s, {ex.Message}).");
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempt, 5)), life);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error($"Bluetooth reconnect stopped: {ex.Message}"); }
    }

    private void TearDown()
    {
        foreach (var c in _chars) c.ValueChanged -= OnValueChanged;
        _chars.Clear();
        if (_device != null) _device.ConnectionStatusChanged -= OnConnectionChanged;
        try { _service?.Dispose(); } catch { /* already gone */ }
        try { _session?.Dispose(); } catch { /* already gone */ }
        try { _device?.Dispose(); } catch { /* already gone */ }
        _service = null;
        _session = null;
        _device = null;
    }

    private void SetConnected(bool connected, string message)
    {
        _linkUp = connected;
        Status = Status with { Connected = connected, Message = message };
        StatusChanged?.Invoke(Status);
    }

    private static async Task<T> WithTimeout<T>(Task<T> op, TimeSpan timeout, CancellationToken ct)
    {
        var done = await Task.WhenAny(op, Task.Delay(timeout, ct));
        ct.ThrowIfCancellationRequested();
        if (done != op) throw new TimeoutException("the patch did not answer");
        return await op;
    }

    private async Task<(ulong Address, string Name)> ScanAsync(CancellationToken ct, int seconds, bool quiet = false)
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
        if (!quiet) Log.Info($"Scanning for a LucidPro patch (up to {seconds} s). Switch the patch on.");
        watcher.Start();
        try
        {
            var done = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(seconds), ct));
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
        Interlocked.Exchange(ref _lastDataAt, receivedAt);
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

    private void OnConnectionChanged(BluetoothLEDevice sender, object args)
    {
        // The supervisor notices the missing data and reconnects; this only reports the link state promptly.
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && _linkUp)
            Log.Info($"{_name}: Bluetooth link dropped.");
    }

    public async Task StopAsync()
    {
        _life?.Cancel();
        if (_supervisor != null)
        {
            try { await _supervisor; }
            catch { /* logged inside */ }
        }
        _supervisor = null;
        _life?.Dispose();
        _life = null;
        foreach (var c in _chars)
        {
            try { await WithTimeout(c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(), TimeSpan.FromSeconds(3), CancellationToken.None); }
            catch (Exception ex) { Log.Warn($"Bluetooth stop: {ex.Message}"); }
        }
        TearDown();
        SetConnected(false, _name);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}