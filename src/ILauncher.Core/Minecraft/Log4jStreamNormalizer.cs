using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ILauncher.Core.Minecraft;

/// <summary>
/// Minecraft (and Forge) write log4j events as XML to stdout when the vanilla
/// client log configuration is used. Launchers normally decode those events and
/// show a human readable console; this class does the same.
/// </summary>
public sealed partial class Log4jStreamNormalizer
{
    private readonly StringBuilder _message = new();
    private readonly StringBuilder _throwable = new();
    private EventHeader? _header;
    private bool _inEvent;
    private bool _capturingMessage;
    private bool _capturingThrowable;

    public IEnumerable<string> Process(string line)
    {
        var trimmed = line.TrimStart();

        if (!_inEvent)
        {
            if (trimmed.StartsWith("<log4j:Event", StringComparison.Ordinal))
            {
                _inEvent = true;
                _message.Clear();
                _throwable.Clear();
                _header = ParseHeader(line);
                return Array.Empty<string>();
            }

            if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<log4j:", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("<log4j:Event", StringComparison.Ordinal))
            {
                return Array.Empty<string>();
            }

            return new[] { line };
        }

        // Inside a log4j event.
        if (trimmed.StartsWith("</log4j:Event", StringComparison.Ordinal))
        {
            var result = Flush();
            _inEvent = false;
            _header = null;
            return result;
        }

        if (trimmed.StartsWith("<log4j:Message", StringComparison.Ordinal))
        {
            _capturingMessage = true;
            _capturingThrowable = false;
            AppendCData(_message, line);
            if (line.Contains("]]>", StringComparison.Ordinal))
            {
                _capturingMessage = false;
            }

            return Array.Empty<string>();
        }

        if (trimmed.StartsWith("<log4j:Throwable", StringComparison.Ordinal))
        {
            _capturingMessage = false;
            _capturingThrowable = true;
            AppendCData(_throwable, line);
            if (line.Contains("]]>", StringComparison.Ordinal))
            {
                _capturingThrowable = false;
            }

            return Array.Empty<string>();
        }

        if (trimmed.StartsWith("<log4j:", StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        if (_capturingMessage)
        {
            _message.AppendLine(line);
            if (line.Contains("]]>", StringComparison.Ordinal))
            {
                _capturingMessage = false;
            }

            return Array.Empty<string>();
        }

        if (_capturingThrowable)
        {
            _throwable.AppendLine(line);
            if (line.Contains("]]>", StringComparison.Ordinal))
            {
                _capturingThrowable = false;
            }

            return Array.Empty<string>();
        }

        // Unexpected text inside an event: keep it as part of the message.
        _message.AppendLine(line);
        return Array.Empty<string>();
    }

    public IEnumerable<string> FlushPending()
    {
        if (!_inEvent)
        {
            return Array.Empty<string>();
        }

        var result = Flush();
        _inEvent = false;
        _header = null;
        return result;
    }

    private string[] Flush()
    {
        var header = _header ?? new EventHeader(DateTime.Now, "INFO", "main", "Minecraft");
        var message = CleanCData(_message.ToString());
        var throwable = CleanCData(_throwable.ToString());
        _message.Clear();
        _throwable.Clear();

        var output = new List<string>();
        var messageLines = SplitLines(message);
        var prefix = $"[{header.Time:HH:mm:ss}] [{header.Thread}/{header.Level}] [{header.Logger}]: ";
        if (messageLines.Count == 0)
        {
            output.Add(prefix.TrimEnd());
        }
        else
        {
            output.Add(prefix + messageLines[0]);
            for (var i = 1; i < messageLines.Count; i++)
            {
                output.Add("    " + messageLines[i]);
            }
        }

        if (!string.IsNullOrWhiteSpace(throwable))
        {
            foreach (var line in SplitLines(throwable))
            {
                output.Add("    " + line);
            }
        }

        return output.ToArray();
    }

    private static List<string> SplitLines(string value)
        => value.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Where(line => line.Length > 0)
            .ToList();

    private static string CleanCData(string value)
    {
        var start = value.IndexOf("<![CDATA[", StringComparison.Ordinal);
        if (start >= 0)
        {
            value = value[(start + 9)..];
        }

        var end = value.IndexOf("]]>", StringComparison.Ordinal);
        if (end >= 0)
        {
            value = value[..end];
        }

        return value.Trim('\n', '\r');
    }

    private static void AppendCData(StringBuilder builder, string line)
    {
        var start = line.IndexOf("<![CDATA[", StringComparison.Ordinal);
        if (start >= 0)
        {
            var content = line[(start + 9)..];
            var end = content.IndexOf("]]>", StringComparison.Ordinal);
            if (end >= 0)
            {
                content = content[..end];
            }

            builder.AppendLine(content);
            return;
        }

        builder.AppendLine(line);
    }

    private static EventHeader ParseHeader(string line)
    {
        var timestamp = ParseLong(Attribute(line, "timestamp"));
        var level = Attribute(line, "level") ?? "INFO";
        var thread = Attribute(line, "thread") ?? "main";
        var logger = Attribute(line, "logger") ?? "Minecraft";
        var time = timestamp > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime
            : DateTime.Now;
        return new EventHeader(time, level.ToUpperInvariant(), thread, logger);
    }

    private static string? Attribute(string line, string name)
    {
        var match = Regex.Match(line, $@"{Regex.Escape(name)}=""(?<v>[^""]*)""");
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static long ParseLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private sealed record EventHeader(DateTime Time, string Level, string Thread, string Logger);
}
