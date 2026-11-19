using System.Globalization;
using System.Text;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Core.Events.Sources;

/// <summary>
/// The hand-over point between a Claude Code hook process and the running island. They are separate
/// processes, and the hook is on Claude Code's critical path, so it must return in milliseconds: it
/// writes one small file and exits. The island watches the folder and consumes it.
/// <para>
/// Atomic by construction: the writer creates a <c>.tmp</c> file and renames it into place, so the
/// reader never sees a half-written report. The reader deletes what it consumed, and anything older
/// than <see cref="MaxAge"/> is discarded, so a dead island cannot build up a backlog.
/// </para>
/// </summary>
public sealed class AgentHookInbox(string directory)
{
    /// <summary>Reports older than this are dropped rather than replayed late.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    private const string Extension = ".json";

    public string Directory { get; } = directory;

    /// <summary>
    /// Writes one report. Never throws: a hook must not fail Claude Code because a folder is missing.
    /// Returns false when the report could not be stored.
    /// </summary>
    public bool Write(AgentHookReport report)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var stamp = report.At.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var unique = Guid.NewGuid().ToString("N")[..8];
            var final = Path.Combine(Directory, $"{stamp}-{unique}{Extension}");
            var temp = final + ".tmp";

            var line = string.Create(CultureInfo.InvariantCulture, $"{{\"kind\":\"{report.Kind}\",\"session\":{Quote(report.SessionId)},\"project\":{Quote(report.Project)},\"tool\":{Quote(report.Tool)},\"at\":\"{report.At:O}\"}}");
            // No BOM: the reader is ours, but a stray BOM has already broken JSON parsing in more
            // tools than it has ever helped.
            File.WriteAllText(temp, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, final, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Warn("events", $"hook report could not be written: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Reads and deletes every pending report, oldest first. A file that cannot be parsed is deleted
    /// as well: retrying a malformed report forever helps nobody.
    /// </summary>
    public IReadOnlyList<AgentHookReport> Drain(DateTimeOffset now)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        List<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(Directory, "*" + Extension)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("events", $"hook folder could not be listed: {ex.GetType().Name}");
            return [];
        }

        var reports = new List<AgentHookReport>(files.Count);
        foreach (var file in files)
        {
            string? text = null;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process may have taken it; the next sweep will not see it either.
                continue;
            }
            TryDelete(file);
            if (text is null)
            {
                continue;
            }

            var report = ParseStored(text);
            if (report is null || now - report.At > MaxAge || now - report.At < -MaxAge)
            {
                continue;
            }

            reports.Add(report);
        }

        return reports;
    }

    /// <summary>Deletes reports left behind by a previous run of the island (nothing would read them).</summary>
    public void ClearStale()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension))
            {
                TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("events", $"hook folder could not be cleared: {ex.GetType().Name}");
        }
    }

    private static AgentHookReport? ParseStored(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != System.Text.Json.JsonValueKind.String
                || !Enum.TryParse<AgentHookKind>(kindElement.GetString(), ignoreCase: true, out var kind))
            {
                return null;
            }

            string? StringOf(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

            var at = DateTimeOffset.TryParse(StringOf("at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            return new AgentHookReport(kind, StringOf("session"), StringOf("project"), at, StringOf("tool"));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the age check on a later sweep.
        }
    }

    private static string Quote(string? value) =>
        value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);
}
