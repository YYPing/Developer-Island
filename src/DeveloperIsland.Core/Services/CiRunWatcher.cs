using System.Text.Json;

namespace DeveloperIsland.Core.Services;

/// <summary>
/// One GitHub Actions run, reduced to what the island shows and acts on.
/// </summary>
/// <param name="Id">The run's database id; stable across status changes.</param>
/// <param name="Conclusion">
/// <c>success</c>, <c>failure</c>, <c>cancelled</c>…; null while the run is still going.
/// </param>
public sealed record CiRun(long Id, string Title, string Status, string? Conclusion, string UpdatedAt, string Url)
{
    /// <summary>Whether the run ended in one of the two outcomes worth telling the user about.</summary>
    public bool IsTerminal => Conclusion is "failure" or "success";

    /// <summary>
    /// Identity for "have I reported this outcome already". Deliberately excludes
    /// <see cref="UpdatedAt"/>: GitHub bumps it for bookkeeping after the fact, and a run that
    /// finished once must not flash again because of it.
    /// </summary>
    public string Key => Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Conclusion;

    /// <summary>The line the Pipeline panel shows for this run.</summary>
    public string Describe() => $"{Title} · {Status} {Conclusion}\n{UpdatedAt}\n{Url}";

    /// <summary>
    /// Parses <c>gh run list --json …</c> output. Throws <see cref="JsonException"/> when the
    /// document is not a JSON array at all (the caller shows an error), but tolerates individual
    /// entries with missing or oddly typed fields: one odd run must not hide the others.
    /// </summary>
    public static IReadOnlyList<CiRun> ParseList(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("gh run list did not return a JSON array.");
        }

        var runs = new List<CiRun>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? Text(string name) =>
                element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            if (!element.TryGetProperty("databaseId", out var idElement)
                || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt64(out var id)
                || id == 0)
            {
                continue;
            }

            var conclusion = Text("conclusion");
            runs.Add(new CiRun(
                id,
                Text("displayTitle") is { Length: > 0 } title ? title : "CI",
                Text("status") is { Length: > 0 } status ? status : "unknown",
                string.IsNullOrEmpty(conclusion) ? null : conclusion,
                Text("updatedAt") ?? string.Empty,
                Text("url") ?? string.Empty));
        }

        return runs;
    }
}

/// <summary>
/// Decides which CI outcomes are news. The first look at a repository is only a baseline: those
/// runs finished before the island was watching, and announcing five old results on startup would
/// teach the user to ignore the capsule. After that, an outcome is announced once — when a run
/// reaches <c>success</c> or <c>failure</c>, or when it reaches a different outcome later (a re-run).
/// <para>
/// The same rule applies again after <see cref="Reset"/> (the user points the island at another
/// repository, or turns the module back on): the new list is history until something new finishes.
/// </para>
/// </summary>
public sealed class CiRunWatcher
{
    /// <summary>How many finished outcomes are remembered, so the set cannot grow forever.</summary>
    public const int MaxRemembered = 200;

    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _order = [];
    private bool _primed;

    /// <summary>Whether the baseline pass has happened (before that, nothing is announced).</summary>
    public bool IsPrimed => _primed;

    /// <summary>How many finished outcomes are currently remembered (for tests and diagnostics).</summary>
    public int RememberedCount => _seen.Count;

    /// <summary>Forgets everything, so the next look is a baseline again.</summary>
    public void Reset()
    {
        _seen.Clear();
        _order.Clear();
        _primed = false;
    }

    /// <summary>
    /// Records the finished runs it is given and returns only those that are news: none on the
    /// baseline pass, then each newly finished outcome exactly once.
    /// </summary>
    public IReadOnlyList<CiRun> Observe(IEnumerable<CiRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var finished = new List<CiRun>();
        foreach (var run in runs)
        {
            if (run.IsTerminal)
            {
                finished.Add(run);
            }
        }

        if (!_primed)
        {
            // History, not news: remember it so a restart does not replay it either.
            foreach (var run in finished)
            {
                Remember(run.Key);
            }

            _primed = true;
            return [];
        }

        var news = new List<CiRun>();
        foreach (var run in finished)
        {
            if (_seen.Contains(run.Key))
            {
                continue;
            }

            Remember(run.Key);
            news.Add(run);
        }

        return news;
    }

    private void Remember(string key)
    {
        if (!_seen.Add(key))
        {
            return;
        }

        _order.Enqueue(key);
        while (_order.Count > MaxRemembered)
        {
            _seen.Remove(_order.Dequeue());
        }
    }
}
