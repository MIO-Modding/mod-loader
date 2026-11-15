using JetBrains.Annotations;
using System.Text;

namespace MioModLoader;

/// <summary>
/// How important a log line is, least to most. Lines below <see cref="ModLoader.MinimumLogLevel"/>
/// (set with the --log-level launch argument, default Info) are dropped from both the log window and
/// the log file. Debug lines are dim and warnings and errors colored in the window; all but Info are
/// tagged in the file.
/// </summary>
[PublicAPI]
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Formats and routes every loader and mod log line.
///
/// Each line is written twice:
///  - to the log window, colored unless the game was started with --plain-logs;
///  - as plain text to the game's log file, through <see cref="ModLoader.FileLogSink"/> (set by the
///    modding api, which can reach the game's log file). Lines logged before the sink can write,
///    such as everything before the game opens its log file, are kept and written once it can.
/// </summary>
internal static class Logging
{
    private const string Reset = "\x1b[0m";
    private const string Gray = "\x1b[90m";
    private const string Yellow = "\x1b[38;2;255;215;95m";
    private const string Red = "\x1b[38;2;255;95;95m";
    private const string LoaderColor = "\x1b[38;2;95;215;255m";

    /// <summary>Colors given to mods, picked by a stable hash of the mod id.</summary>
    private static readonly (byte r, byte g, byte b)[] Palette =
    [
        (135, 215, 135), (255, 175, 95), (175, 135, 255), (255, 135, 175),
        (95, 215, 175), (215, 215, 95), (135, 175, 255), (255, 135, 95),
        (175, 215, 255), (215, 135, 215), (135, 255, 215), (255, 215, 175),
    ];

    private static readonly object Lock = new();
    private static readonly Queue<string> PendingFileLines = new();

    /// <summary>Set from the native loader's flags: colors in the log window.</summary>
    internal static bool Rich { get; set; } = true;

    /// <summary>Writes a finished line to the log window (native, thread-safe).</summary>
    internal static Action<string>? ConsoleSink { get; set; }

    /// <summary>Set from the native loader's flags (--log-level); see <see cref="ModLoader.MinimumLogLevel"/>.</summary>
    internal static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    internal static void Write(string? source, string? sourceColor, LogLevel level, string message)
    {
        if (level < MinimumLevel) return;

        string time = DateTime.Now.ToString("[HH:mm:ss.fff]");
        string[] lines = message.Replace("\r\n", "\n").Split('\n');

        var plain = new StringBuilder();
        var rich = new StringBuilder();
        foreach (string line in lines)
        {
            if (plain.Length > 0) { plain.Append('\n'); rich.Append('\n'); }
            plain.Append(PlainLine(time, source, level, line));
            rich.Append(Rich ? RichLine(time, source, sourceColor, level, line) : PlainLine(time, source, level, line));
        }

        ConsoleSink?.Invoke(rich.ToString());
        WriteToFile(plain.ToString());
    }

    private static string PlainLine(string time, string? source, LogLevel level, string line)
    {
        var sb = new StringBuilder(time);
        if (source != null) sb.Append(" [").Append(source).Append(']');
        if (level == LogLevel.Debug) sb.Append(" [DEBUG]");
        else if (level == LogLevel.Warning) sb.Append(" [WARN]");
        else if (level == LogLevel.Error) sb.Append(" [ERROR]");
        return sb.Append(' ').Append(line).ToString();
    }

    private static string RichLine(string time, string? source, string? sourceColor, LogLevel level, string line)
    {
        var sb = new StringBuilder(Gray).Append(time).Append(Reset);
        if (source != null) sb.Append(" [").Append(sourceColor ?? "").Append(source).Append(Reset).Append(']');
        string? messageColor = level switch
        {
            LogLevel.Debug => Gray,
            LogLevel.Warning => Yellow,
            LogLevel.Error => Red,
            _ => null,
        };
        sb.Append(' ');
        if (messageColor != null) sb.Append(messageColor).Append(line).Append(Reset);
        else sb.Append(line);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ file

    private static void WriteToFile(string plain)
    {
        lock (Lock)
        {
            PendingFileLines.Enqueue(plain);
            FlushLocked();
        }
    }

    /// <summary>Writes any kept lines, if the sink can take them now.</summary>
    internal static void Flush()
    {
        lock (Lock) FlushLocked();
    }

    private static void FlushLocked()
    {
        Func<string, bool>? sink = ModLoader.FileLogSink;
#pragma warning disable CS0618 // an older modding api still sets logOverride
        if (sink == null && ModLoader.logOverride is { } legacy)
        {
            sink = line => { legacy(line); return true; };
        }
#pragma warning restore CS0618
        if (sink == null) return;

        while (PendingFileLines.Count > 0)
        {
            bool written;
            try { written = sink(PendingFileLines.Peek()); }
            catch { written = false; }
            if (!written) return;   // not ready yet (e.g. the game hasn't opened its log); keep for later
            PendingFileLines.Dequeue();
        }
    }

    // ------------------------------------------------------------------ colors

    internal static string LoaderSourceColor => LoaderColor;

    /// <summary>The color for a mod: its mod.json "logColor" ("#RRGGBB") if valid, otherwise one picked from its id.</summary>
    internal static string ColorFor(string id, string? hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#' &&
            int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int rgb))
        {
            return Ansi((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        // FNV-1a: string.GetHashCode is randomized per process, which would change colors every run.
        uint hash = 2166136261;
        foreach (char c in id) hash = (hash ^ c) * 16777619;
        var (r, g, b) = Palette[hash % (uint)Palette.Length];
        return Ansi(r, g, b);
    }

    private static string Ansi(byte r, byte g, byte b) => $"\x1b[38;2;{r};{g};{b}m";
}

/// <summary>
/// Stands in for Console.Out. Standard output now carries the game's own output (see the native
/// loader), so text a mod writes with Console.WriteLine is logged as a "Console" line instead.
/// </summary>
internal sealed class ConsoleLogWriter : TextWriter
{
    private readonly StringBuilder line = new();
    private readonly object sync = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (sync)
        {
            if (value == '\n')
            {
                string text = line.ToString().TrimEnd('\r');
                line.Clear();
                Logging.Write("Console", Logging.LoaderSourceColor, LogLevel.Info, text);
            }
            else
            {
                line.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (value == null) return;
        foreach (char c in value) Write(c);
    }
}