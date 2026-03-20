using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CopilotTerminalFeed.Server;

/// <summary>
/// Lightweight structured logger. Writes timestamped, leveled messages to stdout/stderr.
/// Designed to be dependency-free (no ILogger/DI required) while still providing
/// structured output that can be parsed by log aggregators.
///
/// Format: [2024-01-15T10:30:45.123Z] [INF] [TerminalServer] Message here {key=value}
/// </summary>
public static class Log
{
    public enum Level { Debug, Info, Warning, Error }

    public static Level MinLevel { get; set; } = Level.Info;

    public static void Debug(string message, [CallerMemberName] string? caller = null)
        => Write(Level.Debug, message, caller);

    public static void Info(string message, [CallerMemberName] string? caller = null)
        => Write(Level.Info, message, caller);

    public static void Warn(string message, [CallerMemberName] string? caller = null)
        => Write(Level.Warning, message, caller);

    public static void Error(string message, Exception? ex = null, [CallerMemberName] string? caller = null)
    {
        var msg = ex is not null ? $"{message} | {ex.GetType().Name}: {ex.Message}" : message;
        Write(Level.Error, msg, caller);
    }

    private static void Write(Level level, string message, string? caller)
    {
        if (level < MinLevel) return;

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var tag = level switch
        {
            Level.Debug => "DBG",
            Level.Info => "INF",
            Level.Warning => "WRN",
            Level.Error => "ERR",
            _ => "???"
        };

        var line = $"[{timestamp}] [{tag}] [{caller}] {message}";

        if (level >= Level.Error)
            Console.Error.WriteLine(line);
        else
            Console.WriteLine(line);
    }
}
