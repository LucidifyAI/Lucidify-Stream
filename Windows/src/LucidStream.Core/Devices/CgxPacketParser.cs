namespace LucidStream.Core.Devices;

using LucidStream.Core.Common;

/// <summary>How the three bytes of each sample are packed.</summary>
public enum CgxPacking
{
    /// <summary>
    /// The CGX specification: each byte carries 7 data bits in its upper bits and a 0 in bit 0, so 0xFF only ever
    /// appears as the sync byte. Value = ((msb &lt;&lt; 24) | (lsb2 &lt;&lt; 17) | (lsb1 &lt;&lt; 10)) &gt;&gt; 8.
    /// </summary>
    Cgx7Bit,

    /// <summary>Plain signed 24-bit big-endian, as the Lucidify Android app and Cloud viewer currently decode. For comparison only.</summary>
    Plain24
}

/// <summary>
/// Connection settings for the LucidPro (CGX Patch EEG). Stored in settings.json under "ble".
/// Empty values mean "use the CGX default", so settings files written by older builds keep working.
/// </summary>
public sealed record PatchBleSettings
{
    public const string DefaultServiceUuid = "2456e1b9-26e2-8f83-e744-f34f01e9d701";

    /// <summary>The patch notifies on two characteristics; the Android app enables both and treats them as one byte stream.</summary>
    public static readonly string[] DefaultDataCharacteristicUuids =
    {
        "2456e1b9-26e2-8f83-e744-f34f01e9d703",
        "2456e1b9-26e2-8f83-e744-f34f01e9d704"
    };

    /// <summary>"auto" (CGX USB dongle if one is plugged in, else Bluetooth), "dongle" or "bluetooth".</summary>
    public string Connection { get; init; } = "auto";

    /// <summary>Advertised name prefix also accepted while scanning (the scan matches the CGX service UUID first).</summary>
    public string NamePrefix { get; init; } = "CGX";

    /// <summary>Optional override of the GATT service UUID.</summary>
    public string DataServiceUuid { get; init; } = "";

    /// <summary>Optional override: one data characteristic UUID instead of the two CGX defaults.</summary>
    public string DataCharacteristicUuid { get; init; } = "";

    /// <summary>"cgx" (the CGX specification, default) or "plain24" (how the Android app decodes today; for comparison).</summary>
    public string Packing { get; init; } = "cgx";

    /// <summary>Baud rate of the CGX USB dongle. The CGX Patch data format document lists 1 Mbps, 8-N-1, RTS/CTS.</summary>
    public int DongleBaud { get; init; } = 1_000_000;

    /// <summary>How long to scan for a patch before giving up.</summary>
    public int ScanSeconds { get; init; } = 15;

    public Guid ServiceGuid => Guid.Parse(string.IsNullOrWhiteSpace(DataServiceUuid) ? DefaultServiceUuid : DataServiceUuid);

    public Guid[] DataCharacteristicGuids => string.IsNullOrWhiteSpace(DataCharacteristicUuid)
        ? DefaultDataCharacteristicUuids.Select(Guid.Parse).ToArray()
        : new[] { Guid.Parse(DataCharacteristicUuid) };

    public CgxPacking PackingMode => string.Equals(Packing, "plain24", StringComparison.OrdinalIgnoreCase) ? CgxPacking.Plain24 : CgxPacking.Cgx7Bit;
}

/// <summary>
/// One decoded 36-byte Patch EEG packet, per "Patch EEG Bluetooth Data Interface Specification" (CGX):
///   byte 0      sync 0xFF
///   byte 1      packet counter 0x00-0x7F, +1 per sample, wraps to 0
///   bytes 2-31  ten 3-byte channels: AFz, Fp2, impedance AFz, impedance Fp2, 3 unused, ACC X, Y, Z
///   byte 32     impedance flag (0x12: demodulated in firmware, which is the Patch's normal mode)
///   byte 33     battery, volts = value / 25
///   byte 34     trigger (the Patch has none)
///   byte 35     SD card: 1 present, 0 absent
/// </summary>
public readonly record struct CgxPacket(
    int Counter,
    float AfzUv,
    float Fp2Uv,
    float ImpedanceAfzOhm,
    float ImpedanceFp2Ohm,
    float AccX,
    float AccY,
    float AccZ,
    int ImpedanceFlag,
    float BatteryVolts,
    int SdCard,
    int OddPayloadBytes);

/// <summary>Conversion constants and single-packet decoding for the Patch EEG.</summary>
public static class CgxPatchFormat
{
    public const int PacketSize = 36;
    public const byte Sync = 0xFF;
    public const int CounterModulo = 128;
    public const double SampleRate = 500;

    /// <summary>EEG: 4.8 / (6 * 2^24) volts per count of the 24-bit value (CGX). In microvolts, about 0.0477.</summary>
    public const double MicrovoltsPerCount = 4.8 / (6.0 * 16777216.0) * 1e6;

    /// <summary>Impedance channels (demodulated in firmware): ohms = x * 27.388 - 28922 (CGX).</summary>
    public const double ImpedanceOhmsPerCount = 27.388;
    public const double ImpedanceOffsetOhms = -28922;

    /// <summary>
    /// Accelerometer: g = x * 6.4e-5 (CGX), x being the decoded 24-bit value. The app logs the measured magnitude
    /// once per connection so it can be checked against 1 g at rest.
    /// </summary>
    public const double AccelGPerCount = 6.4e-5;

    public const double BatteryVoltsPerCount = 1.0 / 25.0;

    /// <summary>Patch input range +/-300 mV (CGX user guide). CGX recommends flagging DC offsets beyond this as bad contact.</summary>
    public const double ContactOffsetLimitUv = 300_000;

    /// <summary>Signed 24-bit value of channel <paramref name="channel"/> (0-9) in a packet.</summary>
    public static int Sample24(ReadOnlySpan<byte> packet, int channel, CgxPacking packing)
    {
        var o = 2 + channel * 3;
        int b0 = packet[o], b1 = packet[o + 1], b2 = packet[o + 2];
        return packing == CgxPacking.Cgx7Bit
            ? ((b0 << 24) | (b1 << 17) | (b2 << 10)) >> 8
            : ((b0 << 24) | (b1 << 16) | (b2 << 8)) >> 8;
    }

    /// <summary>Inverse of the CGX packing for one 24-bit value (its lowest 3 bits are dropped, as on the device). Used by tests and simulators.</summary>
    public static void Encode24(int value24, Span<byte> dest)
    {
        var u = (value24 >> 3) & 0x1FFFFF;
        dest[0] = (byte)(((u >> 14) & 0x7F) << 1);
        dest[1] = (byte)(((u >> 7) & 0x7F) << 1);
        dest[2] = (byte)((u & 0x7F) << 1);
    }

    public static CgxPacket Decode(ReadOnlySpan<byte> p, CgxPacking packing)
    {
        var odd = 0;
        for (var i = 2; i < 32; i++) if ((p[i] & 1) != 0) odd++;
        return new CgxPacket(
            Counter: p[1],
            AfzUv: (float)(Sample24(p, 0, packing) * MicrovoltsPerCount),
            Fp2Uv: (float)(Sample24(p, 1, packing) * MicrovoltsPerCount),
            ImpedanceAfzOhm: (float)Math.Max(0, Sample24(p, 2, packing) * ImpedanceOhmsPerCount + ImpedanceOffsetOhms),
            ImpedanceFp2Ohm: (float)Math.Max(0, Sample24(p, 3, packing) * ImpedanceOhmsPerCount + ImpedanceOffsetOhms),
            AccX: (float)(Sample24(p, 7, packing) * AccelGPerCount),
            AccY: (float)(Sample24(p, 8, packing) * AccelGPerCount),
            AccZ: (float)(Sample24(p, 9, packing) * AccelGPerCount),
            ImpedanceFlag: p[32],
            BatteryVolts: (float)(p[33] * BatteryVoltsPerCount),
            SdCard: p[35],
            OddPayloadBytes: odd);
    }

    /// <summary>Battery percent from volts, 3.5 V = 0 % to 4.15 V = 100 % (same mapping as the Lucidify Android app).</summary>
    public static int BatteryPercent(double volts) => (int)Math.Round(Math.Clamp((volts - 3.5) / (4.15 - 3.5), 0, 1) * 100);
}

/// <summary>Raw packets as received, for uploading to Lucidify Cloud unchanged.</summary>
/// <param name="Packets">Concatenated 36-byte packets.</param>
/// <param name="FirstSampleIndex">Sample index of the first packet (continuous across lost packets).</param>
public sealed record CgxRawBlock(byte[] Packets, long FirstSampleIndex, double ReceivedAt)
{
    public int Count => Packets.Length / CgxPatchFormat.PacketSize;
}

/// <summary>
/// Turns the Patch's byte stream (Bluetooth notifications or USB dongle reads, in any split) into EEG, accelerometer
/// and status blocks. Thread-safe: bytes from several characteristics may be fed from different threads.
///
/// - Framing: find 0xFF, take 36 bytes. With CGX packing 0xFF cannot occur inside a packet, so a 0xFF inside means a
///   broken packet and the parser resynchronizes there.
/// - Lost packets: from the 7-bit counter. The patch buffers internally and catches up after short link stalls, so
///   arrival time is only used to size a gap after the source reports the link was lost (<see cref="NoteLinkLost"/>).
/// - EEG: AFz and Fp2 in microvolts at 500 Hz, DC-coupled as delivered.
/// - Accelerometer: averaged to 50 Hz.
/// - Status once per second: battery, SD card, impedance per channel (kOhm), contact (DC offset within +/-300 mV).
/// </summary>
public sealed class CgxStreamParser
{
    public const int AccelDecimation = 10;
    public const double AccelRate = CgxPatchFormat.SampleRate / AccelDecimation;
    private const int StatusEveryPackets = 500;
    private const int CheckAfterPackets = 1000;
    private const double OutageSeconds = 0.5;

    private readonly object _gate = new();
    private readonly CgxPacking _packing;
    private byte[] _buf = new byte[8192];
    private int _head, _tail;

    private int? _lastCounter;
    private double _lastArrival;
    private bool _linkLost;
    private long _nextIndex;

    private readonly double[] _accSum = new double[3];
    private int _accCount;

    private double _impSumA, _impSumF, _dcSumA, _dcSumF, _battSum;
    private int _statusCount;
    private int _sdCard = -1;

    private bool _checksLogged;
    private long _checkOdd;
    private double _checkAccMag, _checkAx, _checkAy, _checkAz;
    private int _checkCount;
    private int _impFlag = -1;

    public CgxStreamParser(CgxPacking packing = CgxPacking.Cgx7Bit) => _packing = packing;

    public CgxPacking Packing => _packing;
    public long PacketsDecoded { get; private set; }
    public long PacketsLost { get; private set; }
    public long BytesDiscarded { get; private set; }
    public long OddPayloadBytes { get; private set; }

    public event Action<EegBlock>? Eeg;
    public event Action<AccelBlock>? Accel;
    public event Action<DeviceStatus>? Status;
    public event Action<CgxRawBlock>? Raw;

    /// <summary>
    /// The Bluetooth link dropped. Packets sent meanwhile are lost, and the gap can exceed one counter cycle, so the next
    /// gap is sized from the time that passed (as the Android app does). Partial bytes from before the drop are discarded.
    /// </summary>
    public void NoteLinkLost()
    {
        lock (_gate)
        {
            _linkLost = true;
            BytesDiscarded += _tail - _head;
            _head = _tail = 0;
        }
    }

    /// <summary>Feed bytes as they arrive. Events fire on the calling thread, after the bytes are parsed.</summary>
    public void Feed(ReadOnlySpan<byte> bytes, double receivedAt)
    {
        List<Group>? groups = null;
        List<float>? acc = null;
        DeviceStatus? status = null;
        lock (_gate)
        {
            Append(bytes);
            var total = 0;
            Group? current = null;
            while (TryTakePacket(out var start))
            {
                var p = new ReadOnlySpan<byte>(_buf, start, CgxPatchFormat.PacketSize);
                var pkt = CgxPatchFormat.Decode(p, _packing);
                var missing = Missing(pkt.Counter, receivedAt);
                if (missing > 0)
                {
                    PacketsLost += missing;
                    _nextIndex += missing;
                    _accCount = 0;
                    Array.Clear(_accSum);
                    current = null;
                }
                if (current == null)
                {
                    current = new Group(_nextIndex, total);
                    (groups ??= new List<Group>()).Add(current);
                }
                current.Add(pkt, p);
                total++;
                _nextIndex++;
                PacketsDecoded++;
                OddPayloadBytes += pkt.OddPayloadBytes;

                if (AddAccel(pkt) is { } a) (acc ??= new List<float>()).AddRange(a);
                status = AddStatus(pkt) ?? status;
                RunChecks(pkt);
            }
            if (groups != null) foreach (var g in groups) g.EndOffset = total - g.FirstOrdinal - g.Count;
        }

        if (groups != null)
        {
            foreach (var g in groups)
            {
                var at = receivedAt - g.EndOffset / CgxPatchFormat.SampleRate;
                Eeg?.Invoke(new EegBlock(g.Eeg.ToArray(), 2, at, g.FirstIndex));
                Raw?.Invoke(new CgxRawBlock(g.Raw.ToArray(), g.FirstIndex, at));
            }
        }
        if (acc != null) Accel?.Invoke(new AccelBlock(acc.ToArray(), receivedAt));
        if (status != null) Status?.Invoke(status);
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (_head > 0 && _tail + bytes.Length > _buf.Length)
        {
            Buffer.BlockCopy(_buf, _head, _buf, 0, _tail - _head);
            _tail -= _head;
            _head = 0;
        }
        if (_tail + bytes.Length > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _tail + bytes.Length));
        bytes.CopyTo(_buf.AsSpan(_tail));
        _tail += bytes.Length;
    }

    private bool TryTakePacket(out int start)
    {
        start = 0;
        while (true)
        {
            var sync = Array.IndexOf(_buf, CgxPatchFormat.Sync, _head, _tail - _head);
            if (sync < 0)
            {
                BytesDiscarded += _tail - _head;
                _head = _tail = 0;
                return false;
            }
            BytesDiscarded += sync - _head;
            _head = sync;
            if (_tail - _head < CgxPatchFormat.PacketSize) return false;
            if (_packing == CgxPacking.Cgx7Bit)
            {
                var inner = Array.IndexOf(_buf, CgxPatchFormat.Sync, _head + 1, CgxPatchFormat.PacketSize - 1);
                if (inner >= 0)
                {
                    BytesDiscarded += inner - _head;
                    _head = inner;
                    continue;
                }
            }
            start = _head;
            _head += CgxPatchFormat.PacketSize;
            return true;
        }
    }

    /// <summary>Packets lost before this one, from the counter (and arrival time after a long outage).</summary>
    private long Missing(int counter, double arrival)
    {
        long missing = 0;
        if (_lastCounter is int last)
        {
            var step = (counter - last + CgxPatchFormat.CounterModulo) % CgxPatchFormat.CounterModulo;
            if (step == 0) step = CgxPatchFormat.CounterModulo;
            missing = step - 1;
            var dt = arrival - _lastArrival;
            if (_linkLost && dt > OutageSeconds)
            {
                // The counter only covers 128 packets; size longer outages from the time that passed.
                var elapsed = dt * CgxPatchFormat.SampleRate;
                var cycles = Math.Max(0, Math.Round((elapsed - step) / CgxPatchFormat.CounterModulo));
                missing += (long)cycles * CgxPatchFormat.CounterModulo;
            }
        }
        _lastCounter = counter;
        _lastArrival = arrival;
        _linkLost = false;
        return missing;
    }

    private float[]? AddAccel(in CgxPacket p)
    {
        _accSum[0] += p.AccX;
        _accSum[1] += p.AccY;
        _accSum[2] += p.AccZ;
        _accCount++;
        if (_accCount < AccelDecimation) return null;
        var r = new[] { (float)(_accSum[0] / _accCount), (float)(_accSum[1] / _accCount), (float)(_accSum[2] / _accCount) };
        _accCount = 0;
        Array.Clear(_accSum);
        return r;
    }

    private DeviceStatus? AddStatus(in CgxPacket p)
    {
        _impSumA += p.ImpedanceAfzOhm;
        _impSumF += p.ImpedanceFp2Ohm;
        _dcSumA += p.AfzUv;
        _dcSumF += p.Fp2Uv;
        _battSum += p.BatteryVolts;
        _sdCard = p.SdCard;
        _statusCount++;
        if (_statusCount < StatusEveryPackets) return null;

        var n = (double)_statusCount;
        var volts = _battSum / n;
        var dcA = _dcSumA / n;
        var dcF = _dcSumF / n;
        var s = new DeviceStatus
        {
            Connected = true,
            BatteryVolts = Math.Round(volts, 2),
            BatteryPercent = CgxPatchFormat.BatteryPercent(volts),
            SdCardPresent = _sdCard switch { 1 => true, 0 => false, _ => null },
            ImpedanceKohm = new double?[] { Math.Round(_impSumA / n / 1000.0, 1), Math.Round(_impSumF / n / 1000.0, 1) },
            Contact = new[] { Math.Abs(dcA) < CgxPatchFormat.ContactOffsetLimitUv, Math.Abs(dcF) < CgxPatchFormat.ContactOffsetLimitUv },
            DcOffsetMv = new double?[] { Math.Round(dcA / 1000.0, 1), Math.Round(dcF / 1000.0, 1) },
            PacketsLost = PacketsLost,
        };
        _impSumA = _impSumF = _dcSumA = _dcSumF = _battSum = 0;
        _statusCount = 0;
        return s;
    }

    /// <summary>One-time checks on real data, written to the log: byte packing, accelerometer scale, impedance mode.</summary>
    private void RunChecks(in CgxPacket p)
    {
        if (_checksLogged) return;
        _checkOdd += p.OddPayloadBytes;
        _checkAccMag += Math.Sqrt(p.AccX * p.AccX + p.AccY * p.AccY + p.AccZ * p.AccZ);
        _checkAx += p.AccX;
        _checkAy += p.AccY;
        _checkAz += p.AccZ;
        _checkCount++;
        if (p.ImpedanceFlag != 0x12 && _impFlag != p.ImpedanceFlag)
        {
            _impFlag = p.ImpedanceFlag;
            Log.Warn($"Patch impedance flag is 0x{p.ImpedanceFlag:X2}; CGX documents 0x12 for the Patch. EEG may contain the impedance carrier.");
        }
        if (_checkCount < CheckAfterPackets) return;
        _checksLogged = true;

        var payloadBytes = _checkCount * 30L;
        if (_packing == CgxPacking.Cgx7Bit)
        {
            if (_checkOdd == 0)
                Log.Info($"Data format check passed: {payloadBytes:N0} payload bytes, none with bit 0 set, as the CGX specification describes.");
            else
                Log.Warn($"Data format check FAILED: {_checkOdd:N0} of {payloadBytes:N0} payload bytes have bit 0 set, which the CGX packing never produces. " +
                         "Tell the Lucid-Stream developers; setting \"packing\": \"plain24\" in settings.json decodes the way the Android app does.");
        }
        Log.Info($"Accelerometer magnitude {_checkAccMag / _checkCount:0.000} g (about 1 g when the patch is still); " +
                 $"mean X {_checkAx / _checkCount:0.000}, Y {_checkAy / _checkCount:0.000}, Z {_checkAz / _checkCount:0.000} g.");
        if (BytesDiscarded > 0) Log.Info($"Resynchronized on the byte stream; {BytesDiscarded:N0} bytes skipped so far.");
    }

    private sealed class Group
    {
        public Group(long firstIndex, int firstOrdinal)
        {
            FirstIndex = firstIndex;
            FirstOrdinal = firstOrdinal;
        }

        public long FirstIndex { get; }
        public int FirstOrdinal { get; }
        public int Count { get; private set; }
        public int EndOffset { get; set; }
        public List<float> Eeg { get; } = new();
        public List<byte> Raw { get; } = new();

        public void Add(in CgxPacket p, ReadOnlySpan<byte> bytes)
        {
            Eeg.Add(p.AfzUv);
            Eeg.Add(p.Fp2Uv);
            foreach (var b in bytes) Raw.Add(b);
            Count++;
        }
    }
}