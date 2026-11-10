using System.Collections.Concurrent;

namespace LucidStream.Core.Common;

public enum LogLevel { Info, Warning, Error }

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time.ToLocalTime():HH:mm:ss} {Level,-7} {Message}";
}

/// <summary>Minimal app log: an event for the UI plus a bounded history for "export log".</summary>
public static class Log
{
    private static readonly ConcurrentQueue<LogEntry> History = new();
    private const int MaxHistory = 2000;

    public static event Action<LogEntry>? Written;

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        History.Enqueue(entry);
        while (History.Count > MaxHistory && History.TryDequeue(out _)) { }
        try { Written?.Invoke(entry); } catch { /* a UI handler must never break streaming */ }
    }

    public static IReadOnlyList<LogEntry> Snapshot() => History.ToArray();
}