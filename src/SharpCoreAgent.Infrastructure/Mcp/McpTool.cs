using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Images;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// One tool a server offered, exposed to the model as an ordinary <see cref="ITool"/>.
/// </summary>
/// <remarks>
/// The adapter is deliberately thin, because everything interesting already happened: the server owns the
/// schema, the behaviour and the answer, and this type's job is to translate names, arguments and results
/// into the shapes round 001 fixed. Nothing here knows MCP exists beyond the prefix it was given, which is
/// why the loop, the approval model and the transcript needed no change to support it.
/// <para>
/// Two rules are worth stating. The schema is forwarded verbatim, because it is the server's contract with
/// the model. And an unannotated tool is destructive: the cost of a needless prompt is one keystroke and
/// the cost of a silent write is the file.
/// </para>
/// </remarks>
public sealed class McpTool : ITool
{
    private readonly McpToolInfo _info;
    private readonly McpServerConnection _connection;
    private readonly McpClientOptions _options;

    /// <summary>
    /// Creates the adapter for one server tool.
    /// </summary>
    /// <param name="name">The <c>&lt;server&gt;_&lt;tool&gt;</c> name the model will call.</param>
    /// <param name="info">What the server said about the tool at startup.</param>
    /// <param name="connection">The running server the call goes to.</param>
    /// <param name="options">The ceilings: the call budget and the result ceiling.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public McpTool(string name, McpToolInfo info, McpServerConnection connection, McpClientOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        _info = info;
        _connection = connection;
        _options = options;

        Definition = new ToolDefinition(name, Describe(info, connection), info.SchemaJson);
        Safety = info.ReadOnly ? ToolSafety.ReadOnly : ToolSafety.Destructive;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; }

    /// <inheritdoc />
    public ToolSafety Safety { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Failures come back as results rather than exceptions, as the tool contract requires: a server that
    /// misbehaves costs one call, and the model reads why instead of the turn ending.
    /// </remarks>
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        // The reader is the argument-object check: a call whose arguments are not an object becomes a
        // readable tool error naming what arrived, before anything is sent to the server.
        var argumentsJson = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson;
        using var arguments = ToolArgumentReader.Parse(Definition.Name, argumentsJson);

        // One sink per call: it holds the artefacts written so far, so the per-call ceiling counts this
        // answer and only this answer.
        var sink = new McpArtefactSink(context.Workspace, context.Approval, _connection.Name, _info.Name, _options);

        var answer = await _connection
            .CallAsync(_info.Name, arguments.Root, _options.CallTimeout, sink.Save, cancellationToken)
            .ConfigureAwait(false);

        // Two ceilings, and this is the first: the tool's own. The loop's MaxToolResultChars still applies
        // to the turn afterwards, and the marker tells the model which one it hit.
        var text = TextLimits.Bound(answer.Text, _options.MaxResultChars, out var truncated);

        if (answer.IsError)
        {
            return ToolResult.Failure(toolCall.Id, Definition.Name, text) with { IsTruncated = truncated };
        }

        // The ceiling and the model gate come after the bound, so the description a server wrote arrives whole and the
        // reasons for what could not be attached are appended to it rather than counted against it.
        var images = Attach(
            context.Model,
            answer.Images,
            context.ImageBytesRemaining,
            context.ImagesAlreadyShown,
            ref text);

        var effects = new List<ToolEffect>();
        if (Safety == ToolSafety.Destructive)
        {
            effects.Add(new ToolEffect(
                ToolEffectKind.CommandRun,
                string.Create(CultureInfo.InvariantCulture, $"{_connection.Name}: {_info.Name}")));
        }

        // A screenshot is annotated read-only by every server that offers one, and it still writes a file.
        // The effects say so, because a turn that reports what it touched must not hide the one write it made.
        foreach (var relative in sink.Written)
        {
            effects.Add(new ToolEffect(ToolEffectKind.Created, relative));
        }

        return ToolResult.Success(toolCall.Id, Definition.Name, text, [.. effects]) with
        {
            IsTruncated = truncated,
            Images = images,
        };
    }

    /// <summary>
    /// Turns the server's image parts into the pictures the loop will attach, and says why the rest were not.
    /// </summary>
    /// <param name="model">The model the conversation is using, or null when the caller did not say.</param>
    /// <param name="images">The parts the server returned, in its order.</param>
    /// <param name="remaining">What the turn may still attach, or null when the caller did not say.</param>
    /// <param name="shown">The pictures the conversation already carries, or null when the caller did not say.</param>
    /// <param name="text">The result text, to which one line per un-attached picture is appended.</param>
    /// <returns>The pictures to attach, in the server's order, at most <see cref="TextLimits.MaxImagesPerResult"/>.</returns>
    /// <remarks>
    /// Six reasons a picture does not travel, and each is written down instead of being dropped: a type no provider
    /// accepts, a payload over the ceiling, a result that already carries its four, an answer that repeats a picture it
    /// has already given, a picture the conversation already carries, and a turn that has spent its budget. Silence would
    /// be worse than any of them — a model told only that an image exists describes what it imagines — so a session whose
    /// model cannot see is told that too, once, in the same shape.
    /// </remarks>
    private static List<ImageContent> Attach(
        ModelDescriptor? model,
        IReadOnlyList<McpImage> images,
        long? remaining,
        IReadOnlySet<ImageContent>? shown,
        ref string text)
    {
        if (images.Count == 0)
        {
            return [];
        }

        if (model is { SupportsImages: false })
        {
            // Only a declared text-only model is worth telling: a caller that named no model at all — a test, a host
            // that never filled the context in — is not a session whose reader was chosen, and round 4's description
            // is already the whole answer for it.
            text = Append(
                text,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{images.Count} image(s) in this result were not attached: {model.QualifiedName} is declared text-only in this build and cannot read images. Do not describe what they contain; name the file and let the developer look.]"));
            return [];
        }

        if (model is not { SupportsImages: true })
        {
            return [];
        }

        var attached = new List<ImageContent>(Math.Min(images.Count, TextLimits.MaxImagesPerResult));
        var notes = new List<string>();
        var left = remaining;

        foreach (var image in images)
        {
            if (!ImagePayload.TryCreate(
                    image.MimeType,
                    image.Base64,
                    TextLimits.MaxImageBytes,
                    out var content,
                    out var reason))
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"[{image.MimeType}: not attached — {reason}]"));
                continue;
            }

            if (attached.Count == TextLimits.MaxImagesPerResult)
            {
                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{content.MediaType}: not attached — this result already carries {TextLimits.MaxImagesPerResult} pictures, which is the most one answer shows]"));
                continue;
            }

            // A server that answers with the same part twice, and a picture the conversation already carries: neither is
            // worth a second copy, because an attached picture is re-sent on every request that follows it. Both are
            // facts about what is already attached rather than about this turn, so they come before the ceiling.
            if (attached.Contains(content))
            {
                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{content.MediaType}: not attached — this result already carries the same picture]"));
                continue;
            }

            if (ImageShown.Already(shown, content))
            {
                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{content.MediaType}: not attached — {ImageShown.Clause}]"));
                continue;
            }

            // The turn's ceiling, last of the six: the first five are facts about this result or about what the
            // conversation holds, and this one is a fact about the turn — which is why the line says the way out is a new
            // turn rather than another call.
            if (left is { } budget && !ImageBudget.Fits(content.ByteCount, budget))
            {
                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{content.MediaType}: not attached — {ImageBudget.Spent(content.ByteCount, budget)}]"));
                continue;
            }

            attached.Add(content);
            left -= content.ByteCount;
        }

        foreach (var note in notes)
        {
            text = Append(text, note);
        }

        return attached;
    }

    private static string Append(string text, string line) => string.Concat(text, "\n\n", line);

    private static string Describe(McpToolInfo info, McpServerConnection connection) =>
        string.IsNullOrWhiteSpace(info.Description)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Call the '{info.Name}' tool of the {connection.Name} MCP server, started with: {connection.CommandLine}")
            : info.Description;
}
