using System.Text;

namespace SharpCoreAgent.Cli;

/// <summary>
/// How the terminal output should decide whether it may use colour.
/// </summary>
internal enum ColorChoice
{
    /// <summary>Colour when the stream is a terminal and the environment has not opted out.</summary>
    Auto,

    /// <summary>Colour even into a pipe or a file, for the developer who wants the escapes captured.</summary>
    Always,

    /// <summary>Never colour, whatever the stream is.</summary>
    Never,
}

/// <summary>
/// The product's only source of ANSI escapes: one palette, one switch, and a plain fallback.
/// </summary>
/// <remarks>
/// Colour is an accessibility feature before it is decoration, so the rules are explicit rather than
/// spread through the renderers. <c>auto</c> leaves the escapes out when the stream is redirected, when
/// <c>NO_COLOR</c> is set (its presence, even empty, is the opt-out the specification defines) or when
/// <c>TERM=dumb</c>; <c>--color always</c> forces them; <c>--color never</c> forbids them. The JSON mode
/// never reaches this type at all, because a machine consumer must not have to strip escapes it never
/// asked for.
/// <para>
/// <see cref="Plain"/> is the same palette with every method returning its argument, which is what lets
/// the renderers be tested as text and lets a host opt out without a null check at every call.
/// </para>
/// </remarks>
internal sealed class AnsiTheme
{
    private const string Reset = "\u001b[0m";

    private AnsiTheme(bool enabled) => Enabled = enabled;

    /// <summary>Gets the no-op theme, for tests, redirects and any host that wants plain text.</summary>
    public static AnsiTheme Plain { get; } = new(enabled: false);

    /// <summary>Gets a value indicating whether this theme emits escapes.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// Builds the theme a command line asked for.
    /// </summary>
    /// <param name="choice">The <c>--color</c> value.</param>
    /// <param name="redirected">Whether the destination is not a terminal.</param>
    /// <param name="environment">The environment reader; defaults to the process environment.</param>
    /// <returns>The theme to render with.</returns>
    public static AnsiTheme Create(ColorChoice choice, bool redirected, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        return new AnsiTheme(choice switch
        {
            ColorChoice.Always => true,
            ColorChoice.Never => false,
            _ => !redirected
                && environment("NO_COLOR") is null
                && !string.Equals(environment("TERM"), "dumb", StringComparison.OrdinalIgnoreCase),
        });
    }

    /// <summary>Renders secondary detail: timing, thinking, elided context.</summary>
    /// <param name="text">The text to paint.</param>
    /// <returns>The painted text, or the text itself when colour is off.</returns>
    public string Dim(string text) => Paint(text, "2");

    /// <summary>Renders the product's own voice: glyphs, prompts, headings.</summary>
    /// <param name="text">The text to paint.</param>
    /// <returns>The painted text, or the text itself when colour is off.</returns>
    public string Accent(string text) => Paint(text, "36");

    /// <summary>Renders a success marker.</summary>
    /// <param name="text">The text to paint.</param>
    /// <returns>The painted text, or the text itself when colour is off.</returns>
    public string Success(string text) => Paint(text, "32");

    /// <summary>Renders a failure marker.</summary>
    /// <param name="text">The text to paint.</param>
    /// <returns>The painted text, or the text itself when colour is off.</returns>
    public string Failure(string text) => Paint(text, "31");

    /// <summary>Renders a warning or an advisory note.</summary>
    /// <param name="text">The text to paint.</param>
    /// <returns>The painted text, or the text itself when colour is off.</returns>
    public string Warning(string text) => Paint(text, "33");

    /// <summary>Renders a name the eye should find first: a tool, a model, a command.</summary>
    /// <param name="text">The text to paint.</param>
    /// <returns>The painted text, or the text itself when colour is off.</returns>
    public string Strong(string text) => Paint(text, "1");

    /// <summary>Renders a workspace path.</summary>
    /// <param name="text">The text to paint.</param>
    /// <returns>The painted text, or the text itself when colour is off.</returns>
    public string Path(string text) => Paint(text, "36");

    /// <summary>
    /// Paints a unified diff line by line: additions green, removals red, hunk headers cyan, file
    /// headers bold and context dim.
    /// </summary>
    /// <param name="diff">The diff text produced by <see cref="SharpCoreAgent.Domain.Text.UnifiedDiff"/>.</param>
    /// <returns>The painted diff, or the diff itself when colour is off.</returns>
    public string Diff(string diff)
    {
        if (!Enabled || string.IsNullOrEmpty(diff))
        {
            return diff;
        }

        var lines = diff.Split('\n');
        var builder = new StringBuilder(diff.Length + (lines.Length * 8));
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length > 0)
            {
                builder.Append(Paint(line, CodeFor(line)));
            }

            if (index < lines.Length - 1)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private string Paint(string text, string code) =>
        Enabled && text.Length > 0 ? string.Concat("\u001b[", code, "m", text, Reset) : text;

    private static string CodeFor(string line) => line switch
    {
        _ when line.StartsWith("+++", StringComparison.Ordinal) => "1",
        _ when line.StartsWith("---", StringComparison.Ordinal) => "1",
        _ when line.StartsWith("@@", StringComparison.Ordinal) => "36",
        _ when line[0] == '+' => "32",
        _ when line[0] == '-' => "31",
        _ => "2",
    };
}
