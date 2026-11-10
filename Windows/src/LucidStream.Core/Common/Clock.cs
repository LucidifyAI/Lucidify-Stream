using System.Diagnostics;
using LucidStream.Core.Lsl;

namespace LucidStream.Core.Common;

/// <summary>
/// The single timebase for every stream: the LSL clock (lsl_local_clock), in seconds.
/// When liblsl is not available the Stopwatch (QueryPerformanceCounter on Windows) is used;
/// liblsl's local_clock is steady_clock, which is also QPC on Windows, so the two agree.
/// </summary>
public static class Clock
{
    private static readonly double TickToSeconds = 1.0 / Stopwatch.Frequency;

    /// <summary>Current time on the LSL clock, seconds.</summary>
    public static double Now => LslNative.IsAvailable ? LslNative.lsl_local_clock() : Stopwatch.GetTimestamp() * TickToSeconds;

    /// <summary>Wall clock in Unix milliseconds, for mapping stream time to calendar time.</summary>
    public static long UtcMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>Injectable time source so token expiry can be unit tested.</summary>
public interface ITimeSource
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemTimeSource : ITimeSource
{
    public static readonly SystemTimeSource Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}