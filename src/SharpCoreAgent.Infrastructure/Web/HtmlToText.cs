using System.Net;
using System.Text;

namespace SharpCoreAgent.Infrastructure.Web;

/// <summary>
/// Reduces an HTML document to the text a reader would see.
/// </summary>
/// <remarks>
/// Written by hand over the BCL for the reason the round exists: a page has to become readable text without adding a
/// dependency, and a regular expression that strips tags is both wrong (a <c>&gt;</c> inside an attribute ends the tag
/// it does not belong to) and a denial-of-service waiting to happen. The scanner below walks the document once, skips
/// the elements that carry no prose, and decodes entities only in the text it keeps — so <c>&amp;lt;b&amp;gt;</c> stays
/// the five characters a reader sees rather than turning into markup on a second pass.
/// <para>
/// This is a readable reduction, not a parser. It does not build a tree, it does not fix a document that is not
/// well-formed, and an unterminated tag simply stops the reduction with the text read so far.
/// </para>
/// </remarks>
public static class HtmlToText
{
    private static readonly string[] BlockTags =
    [
        "address", "article", "aside", "blockquote", "br", "dd", "div", "dl", "dt", "fieldset", "figcaption",
        "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav",
        "ol", "p", "pre", "section", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul",
    ];

    private static readonly string[] SkippedTags = ["head", "script", "style", "svg", "template", "noscript"];

    /// <summary>
    /// Reduces a document to its readable text.
    /// </summary>
    /// <param name="html">The document, decoded.</param>
    /// <returns>The text, with block boundaries as single newlines and runs of whitespace collapsed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
    public static string Reduce(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var text = new StringBuilder(html.Length / 2);
        var index = 0;

        while (index < html.Length)
        {
            var open = html.IndexOf('<', index);
            if (open < 0)
            {
                AppendText(text, html.AsSpan(index));
                break;
            }

            if (open > index)
            {
                AppendText(text, html.AsSpan(index, open - index));
            }

            if (StartsWith(html, open, "<!--"))
            {
                var comment = html.IndexOf("-->", open + 4, StringComparison.Ordinal);
                index = comment < 0 ? html.Length : comment + 3;
                continue;
            }

            var close = FindTagEnd(html, open);
            if (close < 0)
            {
                // An unterminated tag is the end of anything readable; keeping it would print markup to the model.
                break;
            }

            var inner = html.AsSpan(open + 1, close - open - 1).Trim();
            var closing = inner.Length > 0 && inner[0] == '/';
            var name = TagName(closing ? inner[1..] : inner);
            var selfClosing = inner.Length > 0 && inner[^1] == '/';

            if (!closing && !selfClosing && SkippedTags.Contains(name, StringComparer.Ordinal))
            {
                index = SkipElement(html, close + 1, name);
                continue;
            }

            if (BlockTags.Contains(name, StringComparer.Ordinal))
            {
                text.Append('\n');
            }

            index = close + 1;
        }

        return Collapse(text.ToString());
    }

    private static void AppendText(StringBuilder text, ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return;
        }

        // Decoded after the tags have been read, never before: &lt;b&gt; is text a reader sees, not markup to strip.
        // Whitespace inside text is flattened to a space here, because a newline in the source is not a line break a
        // reader sees — only the newline this class writes at a block boundary is.
        foreach (var character in WebUtility.HtmlDecode(span.ToString()))
        {
            text.Append(character is '\r' or '\n' or '\t' or '\f' or '\v' ? ' ' : character);
        }
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var line = new StringBuilder();
        var wrote = false;

        foreach (var character in text)
        {
            if (character == '\n')
            {
                Flush();
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (line.Length > 0 && line[^1] != ' ')
                {
                    line.Append(' ');
                }

                continue;
            }

            line.Append(character);
        }

        Flush();
        return builder.ToString();

        void Flush()
        {
            var content = line.ToString().Trim();
            line.Clear();
            if (content.Length == 0)
            {
                return;
            }

            if (wrote)
            {
                builder.Append('\n');
            }

            builder.Append(content);
            wrote = true;
        }
    }

    private static int FindTagEnd(string html, int open)
    {
        var quote = '\0';
        for (var index = open + 1; index < html.Length; index++)
        {
            var character = html[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (character == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static string TagName(ReadOnlySpan<char> span)
    {
        var end = 0;
        while (end < span.Length && (char.IsLetterOrDigit(span[end]) || span[end] is '-' or ':'))
        {
            end++;
        }

        return span[..end].ToString().ToLowerInvariant();
    }

    private static int SkipElement(string html, int from, string name)
    {
        var close = "</" + name;
        var start = html.IndexOf(close, from, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            // An unclosed script or style at the end of the document: nothing after it was readable anyway.
            return html.Length;
        }

        var end = html.IndexOf('>', start);
        return end < 0 ? html.Length : end + 1;
    }

    private static bool StartsWith(string html, int index, string value) =>
        index + value.Length <= html.Length && string.CompareOrdinal(html, index, value, 0, value.Length) == 0;
}
