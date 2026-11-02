using System.Globalization;
using System.Net;
using System.Text;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The panel: one HTML document rendered from the status document, with no resource it does not carry.
/// </summary>
/// <remarks>
/// A pure function of <see cref="IndexStatusDocument.Build"/>'s output rather than of an <c>IndexStatus</c>, so the page
/// cannot show a number the JSON does not carry and a field added to one cannot go missing from the other. That is also
/// what makes the page assertable without a socket: a test renders a document and reads the HTML.
/// <para>
/// Everything is inline — stylesheet, script, and the icon, which is a <c>data:</c> URI so a browser makes no second
/// request. The script is a literal with no embedded values: it reads the state and the build time out of the
/// <c>data-</c> attributes it compares against, which is why the document needs no escaping rules of its own.
/// </para>
/// </remarks>
internal static class UiPage
{
    /// <summary>
    /// Renders the page for a status document.
    /// </summary>
    /// <param name="document">The document from <see cref="IndexStatusDocument.Build"/>.</param>
    /// <returns>A complete HTML document.</returns>
    /// <exception cref="InvalidOperationException">The document does not carry a field the page reads.</exception>
    internal static string Render(IReadOnlyDictionary<string, object?> document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var state = Text(document, "state");
        var built = OptionalText(document, "builtUtc");
        var html = new StringBuilder();

        html.Append(Head);
        html.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"""
            <body data-state="{Escape(state)}" data-built="{Escape(built)}">
            <main>
              <header>
                <p class="product">sharpcoreagent <span class="dim">· index</span></p>
                <h1><span class="state state--{Escape(state)}" data-field="state">{Escape(state)}</span></h1>
              </header>

            """));

        html.Append(state == "missing" ? Missing : Cards(document, built));

        html.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"""
              <p id="unavailable" class="notice" hidden>the index could not be read just now; these numbers are the ones this page was served with</p>
              <footer>
                <p class="source">every number here is what <code>sharpcoreagent index --status --json</code> prints</p>
                <div class="actions">
                  <button type="button" id="reload">read it again</button>
                  <code class="workspace">{Escape(Text(document, "workspace"))}</code>
                </div>
              </footer>
            </main>
            {Script}
            </body>
            </html>

            """));

        return html.ToString();
    }

    /// <summary>The counts the page shows as cards, in the order they are read.</summary>
    private static readonly string[] BigNumbers = ["files", "passages", "symbols", "edges", "communities"];

    /// <summary>The page for an index that has never been built.</summary>
    /// <remarks>
    /// No cards: a workspace that was never indexed has no counts, and a grid of zeroes would say it does — the developer
    /// would read "0 files" as an empty repository rather than as an unbuilt index. The state above and the one command
    /// that fixes it are the whole useful answer.
    /// </remarks>
    private const string Missing = """
          <section class="empty">
            <p class="hint">run <code>sharpcoreagent index</code> to build it</p>
          </section>

        """;

    /// <summary>
    /// Renders the counts and the health rows.
    /// </summary>
    /// <param name="document">The document from <see cref="IndexStatusDocument.Build"/>.</param>
    /// <param name="built">The build time, or <see langword="null"/> when there has never been a complete build.</param>
    /// <returns>The sections between the heading and the footer.</returns>
    private static string Cards(IReadOnlyDictionary<string, object?> document, string? built)
    {
        var html = new StringBuilder();

        html.Append("  <section class=\"cards\">\n    <dl>\n");
        foreach (var key in BigNumbers)
        {
            html.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"      <div class=\"card\"><dt>{key}</dt><dd data-field=\"{key}\">{IndexStatusDocument.Number(Count(document, key))}</dd></div>\n"));
        }

        html.Append("    </dl>\n  </section>\n\n  <section class=\"detail\">\n    <dl>\n");
        html.Append(Row("tier", "tier", Text(document, "tier")));
        html.Append(Row("embedder", "embedder", Text(document, "embedder")));
        html.Append(Row("dimensions", "dimensions", Count(document, "dimensions").ToString(CultureInfo.InvariantCulture)));
        html.Append(Row("builtUtc", "built", built ?? "never"));
        html.Append(Row("sizeBytes", "size", IndexStatusDocument.Size(Bytes(document, "sizeBytes"))));
        html.Append(Row("vectorSearch", "vector", Flag(document, "vectorSearch") ? "on" : "off"));
        html.Append(Row("stale", "stale", IndexStatusDocument.Number(Count(document, "stale"))));
        html.Append(Row("skipped", "skipped", IndexStatusDocument.Number(Count(document, "skipped"))));
        html.Append(Row("orphans", "orphans", IndexStatusDocument.Number(Count(document, "orphans"))));
        html.Append("    </dl>\n  </section>\n");

        // `incomplete` is a status field rather than a number: a build in flight means the counts above are the last
        // complete build's, which is true and worth saying before a developer reads them as the current tree.
        if (Flag(document, "incomplete"))
        {
            html.Append("  <p class=\"notice\">a build is in progress; these are the last complete build's numbers</p>\n");
        }

        return html.ToString();
    }

    /// <summary>Reads a string field the page must have.</summary>
    /// <exception cref="InvalidOperationException">The document does not carry it, which means the page and the document disagree.</exception>
    private static string Text(IReadOnlyDictionary<string, object?> document, string key) =>
        document.TryGetValue(key, out var value) && value is string text ? text : throw Absent(key);

    /// <summary>Reads a field that may be null, such as the build time of an index with no complete build.</summary>
    private static string? OptionalText(IReadOnlyDictionary<string, object?> document, string key) =>
        document.TryGetValue(key, out var value) ? value as string : null;

    /// <summary>Reads a count.</summary>
    private static long Count(IReadOnlyDictionary<string, object?> document, string key) =>
        document.TryGetValue(key, out var value) && value is int number ? number : throw Absent(key);

    /// <summary>Reads a byte count, which the document stores as a <see cref="long"/>.</summary>
    private static long Bytes(IReadOnlyDictionary<string, object?> document, string key) =>
        document.TryGetValue(key, out var value) && value is long bytes ? bytes : throw Absent(key);

    /// <summary>Reads a switch.</summary>
    private static bool Flag(IReadOnlyDictionary<string, object?> document, string key) =>
        document.TryGetValue(key, out var value) && value is bool flag ? flag : throw Absent(key);

    /// <summary>The failure a page cannot render without a field.</summary>
    private static InvalidOperationException Absent(string key) =>
        new($"the status document has no '{key}' for the page to render.");

    /// <summary>Encodes a value for HTML text or an attribute.</summary>
    /// <param name="value">The value, or <see langword="null"/> for an empty attribute.</param>
    /// <returns>The encoded text.</returns>
    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// The document up to the opening <c>body</c>: the doctype, the metadata, the icon and the stylesheet.
    /// </summary>
    /// <remarks>
    /// The icon is a <c>data:</c> URI with no content, so a browser asks for nothing; the stylesheet is here rather than
    /// linked for the same reason. The palette is light by default and dark under <c>prefers-color-scheme: dark</c>: both
    /// schemes are defined, the muted greys and the state colours are chosen to stay above 4.5:1 against their own
    /// background (17:1 and 15:1 for body text), and nothing assumes a white page — the light values are overridden, not
    /// painted over.
    /// </remarks>
    private const string Head = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="color-scheme" content="light dark">
        <title>sharpcoreagent · index</title>
        <link rel="icon" href="data:,">
        <style>
          :root {
            color-scheme: light dark;
            --bg: #ffffff;
            --panel: #f6f6f8;
            --border: #d8d8e0;
            --fg: #1b1b1f;
            --muted: #5a5a66;
            --accent: #1f5fa8;
            --fresh: #1a6b3c;
            --stale: #8a5300;
            --problem: #781212;
          }
          @media (prefers-color-scheme: dark) {
            :root {
              --bg: #16161a;
              --panel: #1d1d23;
              --border: #2c2c34;
              --fg: #e8e8ee;
              --muted: #a8a8b4;
              --accent: #7fb2ff;
              --fresh: #5fce8e;
              --stale: #e0a45a;
              --problem: #ff8f8f;
            }
          }
          * { box-sizing: border-box; }
          body {
            margin: 0;
            background: var(--bg);
            color: var(--fg);
            font: 15px/1.5 ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif;
          }
          main { max-width: 54rem; margin: 0 auto; padding: 2.5rem 1.5rem 3rem; }
          .product { margin: 0; color: var(--muted); font-size: .8rem; letter-spacing: .07em; text-transform: uppercase; }
          h1 { margin: .2rem 0 1.75rem; font-size: 1.9rem; font-weight: 600; }
          .state { padding: .05rem .6rem; border: 1px solid var(--border); border-radius: .5rem; background: var(--panel); }
          .state--fresh { color: var(--fresh); }
          .state--stale, .state--partial, .state--building { color: var(--stale); }
          .state--missing, .state--corrupt, .state--unknown { color: var(--problem); }
          dl { margin: 0; }
          .cards dl { display: grid; grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr)); gap: .75rem; }
          .card { padding: .9rem 1rem; background: var(--panel); border: 1px solid var(--border); border-radius: .6rem; }
          .card dt { color: var(--muted); font-size: .72rem; letter-spacing: .07em; text-transform: uppercase; }
          .card dd { margin: .3rem 0 0; font-size: 1.55rem; font-variant-numeric: tabular-nums; }
          .detail { margin-top: 1.75rem; }
          .detail dl { border-top: 1px solid var(--border); }
          .row { display: flex; gap: 1rem; padding: .45rem 0; border-bottom: 1px solid var(--border); }
          .row dt { flex: 0 0 8rem; color: var(--muted); }
          .row dd { margin: 0; overflow-wrap: anywhere; font-variant-numeric: tabular-nums; }
          .hint, .notice {
            margin: 1.5rem 0 0;
            padding: .85rem 1rem;
            background: var(--panel);
            border-left: 3px solid var(--accent);
            border-radius: 0 .4rem .4rem 0;
          }
          .notice { border-left-color: var(--stale); }
          code { font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; }
          footer {
            display: flex;
            flex-direction: column;
            gap: .75rem;
            margin-top: 2.5rem;
            padding-top: 1.25rem;
            border-top: 1px solid var(--border);
          }
          .source { margin: 0; color: var(--muted); font-size: .85rem; }
          .actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 1rem; }
          .workspace { color: var(--muted); font-size: .85rem; overflow-wrap: anywhere; }
          button {
            padding: .4rem .9rem;
            font: inherit;
            color: var(--fg);
            background: var(--panel);
            border: 1px solid var(--border);
            border-radius: .45rem;
            cursor: pointer;
          }
          button:hover { border-color: var(--accent); color: var(--accent); }
          button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
          button:disabled { opacity: .6; cursor: progress; }
        </style>
        </head>

        """;


    /// <param name="key">The document field this row shows, which the page marks so a test can find it.</param>
    /// <param name="label">The word in the left column.</param>
    /// <param name="value">The already-formatted value.</param>
    /// <returns>The row.</returns>
    private static string Row(string key, string label, string value) =>
        string.Create(CultureInfo.InvariantCulture, $"      <div class=\"row\"><dt>{label}</dt><dd data-field=\"{key}\">{Escape(value)}</dd></div>\n");

    /// <summary>
    /// The one script: one fetch on load, one per press of the button, and no other request.
    /// </summary>
    /// <remarks>
    /// The page is rendered by the server and the script only decides whether to ask for a new one, so no formatting rule
    /// is written twice: a press re-reads <c>/api/status.json</c> and reloads only when the state or the build time has
    /// moved, and the numbers then come from the server's own renderer. That is also why the load-time check never
    /// reloads — an index being rebuilt would otherwise have the page reloading in a loop, and a panel that flickers is
    /// worse than one that is a click behind. The two values it compares are read from the <c>data-</c> attributes, so
    /// nothing is interpolated into this script and no quoting rule of its own applies.
    /// </remarks>
    private const string Script = """
        <script>
          (() => {
            const state = document.body.dataset.state;
            const built = document.body.dataset.built || '';
            const notice = document.getElementById('unavailable');
            const button = document.getElementById('reload');

            async function recheck(reloadWhenChanged) {
              button.disabled = true;
              try {
                const response = await fetch('/api/status.json', { cache: 'no-store' });
                if (!response.ok) {
                  throw new Error('status ' + response.status);
                }

                const status = await response.json();
                const changed = status.state !== state || (status.builtUtc || '') !== built;
                if (changed && reloadWhenChanged) {
                  location.reload();
                  return;
                }

                notice.hidden = true;
              } catch (failure) {
                notice.hidden = false;
              } finally {
                button.disabled = false;
              }
            }

            button.addEventListener('click', () => recheck(true));
            recheck(false);
          })();
        </script>

        """;
}
