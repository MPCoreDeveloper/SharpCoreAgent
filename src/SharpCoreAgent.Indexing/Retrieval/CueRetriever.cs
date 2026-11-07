using System.Globalization;
using System.Text.RegularExpressions;
using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>
/// Finds framework cues in the source text the index already holds, as cited candidates.
/// </summary>
/// <remarks>
/// This is a scan, not an extractor: it runs over the passages loaded for the build and matches a small, fixed table of
/// regular expressions against each line. That is the whole design decision — a query-time scan needs no new table,
/// no writer change and no <c>analyzer_contract</c> bump, so it cannot stale or rebuild an index, and it never touches
/// the C#/VB heuristic extractor the constitution protects. The cost is one pass over the passages per call, bounded by
/// the caller's cap and paid only when the model asks.
/// <para>
/// Every result is a candidate. A regular expression over source is a guess, so the cue names the written expression
/// (<c>app.get</c>) rather than a resolved symbol, and the citation lets a reader check the guess at the line itself.
/// </para>
/// </remarks>
internal static class CueRetriever
{
    /// <summary>The hard cap on cues one query returns, whatever the caller asked for.</summary>
    internal const int MaxResults = 100;

    private const int SnippetWidth = 160;

    /// <summary>A per-match ceiling, so a pathological line cannot hang the scan (Sonar S6444).</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex DotSpacing = new(
        @"\s*\.\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Rule[] Rules =
    [
        new(CueKinds.Route, Rx(@"\b(?<target>[A-Za-z_]\w*(?:\.\w+)*\s*\.\s*(?:get|post|put|patch|delete|use|all|route|HandleFunc|Handle|register))\s*\(\s*(?:['""](?<detail>[^'""]+)['""])?")),
        new(CueKinds.Route, Rx(@"@(?<target>\w+\.(?:route|get|post|put|delete|patch))\s*\(\s*(?:['""](?<detail>[^'""]+)['""])?")),
        new(CueKinds.Route, Rx(@"@(?<target>(?:Get|Post|Put|Patch|Delete|Request)Mapping)\s*\(\s*(?:value\s*=\s*)?['""](?<detail>[^'""]+)['""]")),

        new(CueKinds.Test, Rx(@"\b(?<target>describe|it|test|context|suite)\s*\(\s*(?:['""](?<detail>[^'""]+)['""])?")),
        new(CueKinds.Test, Rx(@"\b(?:async\s+)?def\s+(?<target>test_\w+)")),
        new(CueKinds.Test, Rx(@"\bfunc\s+(?<target>Test\w+)")),
        new(CueKinds.Test, Rx(@"@(?<target>Test|ParameterizedTest)\b")),

        new(CueKinds.DependencyInjection, Rx(@"\b(?<target>Depends|inject|provide)\s*\(")),
        new(CueKinds.DependencyInjection, Rx(@"@(?<target>Injectable|Inject|Autowired)\b")),

        new(CueKinds.DatabaseCall, Rx(@"\b(?<target>[A-Za-z_]\w*(?:\.\w+)*\s*\.\s*(?:query|execute|executemany|executescript|fetchone|fetchall|find|insert|update|delete|raw))\s*\(")),
        new(CueKinds.DatabaseCall, Rx(@"\b(?<target>create\s+table|insert\s+into|delete\s+from|update\s+\w+\s+set|select\s+.+?\s+from)\b")),
    ];

    /// <summary>
    /// Finds the cues that answer one request.
    /// </summary>
    /// <param name="snapshot">The build being scanned, with its passages loaded.</param>
    /// <param name="kind">One of <see cref="CueKinds"/>, in canonical form; <see cref="CueKinds.Any"/> for all.</param>
    /// <param name="text">An optional term the target, path or detail must contain, or <see langword="null"/>.</param>
    /// <param name="maxResults">The requested cap; it is lowered to <see cref="MaxResults"/> when larger.</param>
    /// <returns>The cues, ordered by path then line; empty when nothing matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> or <paramref name="kind"/> is null.</exception>
    public static IReadOnlyList<CodeCueRef> Find(IndexSnapshot snapshot, string kind, string? text, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(kind);

        if (maxResults <= 0)
        {
            return [];
        }

        var cap = Math.Min(maxResults, MaxResults);
        var anyKind = string.Equals(kind, CueKinds.Any, StringComparison.Ordinal);
        var needle = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        var results = new List<CodeCueRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chunk in snapshot.Chunks.Values.OrderBy(static chunk => chunk.Id))
        {
            if (results.Count >= cap)
            {
                break;
            }

            var lines = chunk.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

            for (var index = 0; index < lines.Length && results.Count < cap; index++)
            {
                var line = lines[index];
                if (line.Length == 0)
                {
                    continue;
                }

                foreach (var rule in Rules)
                {
                    if (results.Count >= cap)
                    {
                        break;
                    }

                    if (!anyKind && !string.Equals(rule.Kind, kind, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Match match;
                    try
                    {
                        match = rule.Pattern.Match(line);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        continue;
                    }

                    if (!match.Success)
                    {
                        continue;
                    }

                    var target = Normalize(match.Groups["target"].Value);
                    if (target.Length == 0)
                    {
                        continue;
                    }

                    var detail = match.Groups["detail"].Success ? match.Groups["detail"].Value : null;
                    if (needle is not null && !MatchesText(needle, target, chunk.Path, detail))
                    {
                        continue;
                    }

                    var absoluteLine = chunk.StartLine + index;
                    var key = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{chunk.Path}\u0000{absoluteLine}\u0000{rule.Kind}\u0000{target}");

                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    results.Add(new CodeCueRef
                    {
                        Kind = rule.Kind,
                        Path = chunk.Path,
                        Line = absoluteLine,
                        Target = target,
                        Detail = detail,
                        Symbol = chunk.SymbolName,
                        Snippet = Truncate(line.Trim()),
                    });
                }
            }
        }

        results.Sort(static (left, right) =>
        {
            var path = string.CompareOrdinal(left.Path, right.Path);
            if (path != 0)
            {
                return path;
            }

            var line = left.Line.CompareTo(right.Line);
            return line != 0 ? line : string.CompareOrdinal(left.Kind, right.Kind);
        });

        return results;
    }

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);

    private static string Normalize(string value) => DotSpacing.Replace(value.Trim(), ".");

    private static bool MatchesText(string needle, string target, string path, string? detail) =>
        target.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || path.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || (detail is not null && detail.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string line) =>
        line.Length <= SnippetWidth ? line : string.Concat(line[..SnippetWidth], "…");

    private readonly record struct Rule(string Kind, Regex Pattern);
}
