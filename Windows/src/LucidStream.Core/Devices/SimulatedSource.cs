using System.Diagnostics;
using LucidStream.Core.Common;

namespace LucidStream.Core.Devices;

/// <summary>
/// Developer source that behaves like the patch: 2 EEG channels at 500 Hz in 10-sample BLE-sized packets,
/// a 3-axis accelerometer, impedance and battery. The signal alternates 30 s "eyes open" and "eyes closed"
/// blocks (alpha rises when closed) and adds blinks, so a sonification page has something to react to.
/// </summary>
public sealed class SimulatedSource : IEegSource
{
    public const double EegRate = 500;
    public const double AccelRateHz = 25;
    private const int SamplesPerPacket = 10;

    private readonly double _packetLossProbability;
    private readonly Random _rng;
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    public SimulatedSource(double packetLossProbability = 0, int seed = 1234)
    {
        _packetLossProbability = packetLossProbability;
        _rng = new Random(seed);
    }

    public string DisplayName => "Simulated patch";
    public DeviceInfo? Info { get; private set; }
    public bool IsReplay => false;
    public ReplayDescriptor? Replay => null;
    public DeviceStatus Status { get; private set; } = new() { Connected = false };

    public event Action<EegBlock>? EegReceived;
    public event Action<AccelBlock>? AccelReceived;
    public event Action<DeviceStatus>? StatusChanged;
    public event Action<SourceMarker>? MarkerReceived { add { } remove { } }

    public Task StartAsync(CancellationToken ct)
    {
        Info = DeviceInfo.LucidPro("SIM-0001", "sim-1.0", AccelRateHz);
        Status = new DeviceStatus
        {
            Connected = true,
            BatteryPercent = 87,
            ImpedanceKohm = new double?[] { 9.5, 11.2 },
            Contact = new[] { true, true },
            Message = "Simulated patch"
        };
        StatusChanged?.Invoke(Status);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "SimulatedPatch", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        Log.Info("Simulated patch connected.");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _thread?.Join(1000);
        _thread = null;
        Status = Status with { Connected = false };
        StatusChanged?.Invoke(Status);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void Run(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long generated = 0;
        long accelGenerated = 0;
        var gen = new SignalGenerator(_rng);
        var lastStatus = 0.0;

        while (!ct.IsCancellationRequested)
        {
            var elapsed = sw.Elapsed.TotalSeconds;
            var due = (long)(elapsed * EegRate);
            while (due - generated >= SamplesPerPacket)
            {
                var samples = new float[SamplesPerPacket * 2];
                for (var i = 0; i < SamplesPerPacket; i++)
                {
                    var (a, b) = gen.Next((generated + i) / EegRate);
                    samples[i * 2] = a;
                    samples[i * 2 + 1] = b;
                }
                var counter = generated;
                generated += SamplesPerPacket;
                if (_packetLossProbability > 0 && _rng.NextDouble() < _packetLossProbability) continue; // dropped "BLE" packet
                EegReceived?.Invoke(new EegBlock(samples, 2, Clock.Now, counter));
            }

            var accDue = (long)(elapsed * AccelRateHz);
            if (accDue > accelGenerated)
            {
                var n = (int)(accDue - accelGenerated);
                var acc = new float[n * 3];
                for (var i = 0; i < n; i++)
                {
                    acc[i * 3] = (float)(0.02 * (_rng.NextDouble() - 0.5));
                    acc[i * 3 + 1] = (float)(0.02 * (_rng.NextDouble() - 0.5));
                    acc[i * 3 + 2] = (float)(0.98 + 0.02 * (_rng.NextDouble() - 0.5));
                }
                accelGenerated = accDue;
                AccelReceived?.Invoke(new AccelBlock(acc, Clock.Now));
            }

            if (elapsed - lastStatus >= 30)
            {
                lastStatus = elapsed;
                var battery = Math.Max(5, 87 - (int)(elapsed / 600));
                Status = Status with { BatteryPercent = battery };
                StatusChanged?.Invoke(Status);
            }

            Thread.Sleep(2);
        }
    }

    /// <summary>Synthetic forehead EEG: pink-ish background, block-wise alpha, a little line noise and blinks.</summary>
    private sealed class SignalGenerator
    {
        private readonly Random _rng;
        private readonly double[] _pink = new double[2];
        private readonly double[] _brown = new double[2];
        private double _nextBlink = 3;

        public SignalGenerator(Random rng) => _rng = rng;

        public (float, float) Next(double t)
        {
            var eyesClosed = ((int)(t / 30)) % 2 == 1;
            var alphaAmp = eyesClosed ? 14.0 : 3.0;
            alphaAmp *= 0.75 + 0.25 * Math.Sin(2 * Math.PI * 0.2 * t); // waxing and waning
            var theta = 2.5 * Math.Sin(2 * Math.PI * 6 * t + 1.3);
            var beta = 1.5 * Math.Sin(2 * Math.PI * 21 * t + 0.4);
            var line = 0.8 * Math.Sin(2 * Math.PI * 60 * t);

            var blink = 0.0;
            if (t >= _nextBlink && t < _nextBlink + 0.3)
            {
                var x = (t - _nextBlink) / 0.3;
                blink = 120 * Math.Sin(Math.PI * x);
            }
            else if (t >= _nextBlink + 0.3)
            {
                _nextBlink = t + 2 + 5 * _rng.NextDouble();
            }

            var outv = new float[2];
            for (var ch = 0; ch < 2; ch++)
            {
                var white = Gauss() * 4.0;
                _pink[ch] = 0.97 * _pink[ch] + 0.25 * white;
                _brown[ch] = 0.999 * _brown[ch] + 0.05 * white;
                var alpha = alphaAmp * Math.Sin(2 * Math.PI * (10 + 0.2 * ch) * t + ch * 0.5);
                outv[ch] = (float)(_pink[ch] + _brown[ch] + alpha + theta + beta + line + blink * (ch == 0 ? 1.0 : 0.9));
            }
            return (outv[0], outv[1]);
        }

        private double Gauss()
        {
            var u1 = 1.0 - _rng.NextDouble();
            var u2 = _rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }
}