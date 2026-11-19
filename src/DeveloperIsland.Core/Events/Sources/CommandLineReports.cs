

namespace DeveloperIsland.Core.Events.Sources;

/// <summary>
/// Reads a Claude Code hook payload from a stream. Separated from the executable so the reading
/// itself is testable: the app is a GUI-subsystem binary, and its standard input behaves differently
/// depending on how it was started, which is exactly the kind of thing that silently produced no
/// reports until it was checked end to end.
/// </summary>
public static class HookInput
{
    private const int MaxChars = 1_000_000;

    /// <summary>
    /// Reads the whole stream as text, giving up after <paramref name="timeout"/>. Returns what was
    /// read, which may be empty: a hook must never wait on a stream that nobody will close.
    /// <para>
    /// Uses the stream's own read-to-end: this is called with <c>Console.In</c> from a GUI-subsystem
    /// process, where a hand-rolled read loop returns nothing at all. The read-to-end path is the one
    /// that has been observed to work there.
    /// </para>
    /// </summary>
    public static string Read(System.IO.TextReader reader, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(reader);

        try
        {
            var text = reader.ReadToEndAsync().WaitAsync(timeout).GetAwaiter().GetResult();
            return text.Length > MaxChars ? text[..MaxChars] : text;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or AggregateException or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// The <c>--report</c> command line: any local script can put one line on the island without
/// linking to the app. The parsing lives here, apart from the executable, so the argument rules are
/// covered by tests rather than by hoping.
/// </summary>
public static class CommandLineReports
{
    /// <summary>
    /// Parses <c>--report &lt;kind&gt; &lt;title&gt; [detail] [--failed]</c>. Returns null when the
    /// arguments do not form a report (no <c>--report</c>, or too few values); the caller then
    /// continues to the next command-line entry point.
    /// </summary>
    /// <param name="invalid">
    /// True when <c>--report</c> was present but the arguments were wrong, so the caller can return a
    /// failure code instead of silently starting the app.
    /// </param>
    public static (string Kind, string Title, string Detail, bool Failed)? ParseCommandLine(
        IReadOnlyList<string> args,
        out bool invalid)
    {
        ArgumentNullException.ThrowIfNull(args);
        invalid = false;

        var index = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], "--report", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return null;
        }

        // A flag is never taken as the detail text: "--failed" must not become the message.
        var values = args.Skip(index + 1)
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .ToList();

        if (values.Count < 2)
        {
            invalid = true;
            return null;
        }

        var failed = args.Any(a => string.Equals(a, "--failed", StringComparison.OrdinalIgnoreCase));
        return (values[0], values[1], values.Count > 2 ? values[2] : string.Empty, failed);
    }

    public static string UsageText() =>
        "usage: DeveloperIsland.exe --report <pipeline|agent|message> <title> [detail] [--failed]";
}
