using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Model;

namespace SharpCoreAgent.Infrastructure.Providers;

/// <summary>
/// An offline provider that answers from a fixed script instead of a network model.
/// </summary>
/// <remarks>
/// The port is only worth having if a second implementation can satisfy it, and this is the cheapest
/// honest one: it streams text, asks for a tool when the prompt calls for one, and answers the tool
/// result like a model would. That makes the quickstart, the CLI and the integration test runnable
/// with no credential and no network, and it keeps live calls out of the test suite.
/// <para>
/// The script is deterministic on purpose: the same transcript always produces the same events, so a
/// test can assert on them and a developer can reproduce a bug from a session file.
/// </para>
/// </remarks>
public sealed partial class DemoChatModel : IStreamingChatModel
{
    private const int ChunkSize = 32;

    /// <summary>Creates the adapter for a descriptor the caller already resolved.</summary>
    /// <param name="model">The descriptor to speak for; the catalogue default when null.</param>
    public DemoChatModel(ModelDescriptor? model = null) => Model = model ?? Catalog[0];

    /// <summary>Gets the provider key. <c>demo</c>.</summary>
    public const string ProviderKey = "demo";

    /// <summary>Gets the model identifier.</summary>
    public const string DefaultModelId = "demo-scripted";

    /// <summary>
    /// Gets the configuration keys this provider needs. Empty: it is the offline provider, which is
    /// what lets the quickstart run before anyone has an account.
    /// </summary>
    public static IReadOnlyList<string> CredentialNames { get; } = [];

    /// <summary>Gets the catalogue, which exists so <c>models</c> can list it like any other provider.</summary>
    public static IReadOnlyList<ModelDescriptor> Catalog { get; } =
    [
        new(ProviderKey, DefaultModelId, "Demo (scripted)", ContextWindow: 8_192),
    ];

    /// <inheritdoc />
    public ModelDescriptor Model { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Yield once so the caller's own async plumbing runs before the first event, which keeps this
        // adapter's behaviour identical to one that waits on a socket.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var prompt = LastUserPrompt(request);
        var lastMessage = request.Messages.Count > 0 ? request.Messages[^1] : null;

        if (lastMessage is { Role: ChatRole.Tool })
        {
            var answer = Summarize(lastMessage);
            foreach (var chunk in Chunk(answer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new TextDelta(chunk);
            }

            yield return new StreamCompleted(new ChatResponse(
                ChatMessage.Assistant(answer),
                FinishReason.Stop,
                EstimateUsage(request, answer)));
            yield break;
        }

        if (SelectTool(request, prompt) is { } call)
        {
            // No text before a tool call: a scripted model that also narrates makes the CLI output
            // depend on which branch ran first.
            yield return new StreamCompleted(new ChatResponse(
                ChatMessage.Assistant(string.Empty, [call]),
                FinishReason.ToolCalls,
                EstimateUsage(request, string.Empty)));
            yield break;
        }

        var text = prompt.Length == 0
            ? "The demo provider answers offline. Give it a prompt, or ask it to list the files."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"The demo provider answered without calling a tool. You said: \"{Shorten(prompt)}\". Ask it to list the files to see a tool run.");

        foreach (var chunk in Chunk(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TextDelta(chunk);
        }

        yield return new StreamCompleted(new ChatResponse(
            ChatMessage.Assistant(text),
            FinishReason.Stop,
            EstimateUsage(request, text)));
    }

    private static string LastUserPrompt(ChatRequest request)
    {
        for (var i = request.Messages.Count - 1; i >= 0; i--)
        {
            if (request.Messages[i].Role == ChatRole.User)
            {
                return request.Messages[i].Content.Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Picks the tool call the prompt asks for, if the agent advertised it.
    /// </summary>
    /// <remarks>
    /// The order matters: "write to notes.txt" must not be read as the read branch, and "list the files"
    /// is the case the quickstart exercises. Each branch is a substring test, which is why the whole
    /// script fits in one method.
    /// </remarks>
    private static ToolCall? SelectTool(ChatRequest request, string prompt)
    {
        if (prompt.Length == 0 || request.Tools.Count == 0)
        {
            return null;
        }

        var id = string.Create(CultureInfo.InvariantCulture, $"demo-call-{request.Messages.Count}");

        if (Advertises(request, "list_directory") && MentionsAny(prompt, "list", "files", "directory", "folders"))
        {
            return new ToolCall(id, "list_directory", "{}");
        }

        if (Advertises(request, "write_file") && MentionsAny(prompt, "write", "create", "save"))
        {
            return new ToolCall(
                id,
                "write_file",
                Arguments(
                    ("path", PathLike().Match(prompt) is { Success: true } match ? match.Value : "notes.txt"),
                    ("content", "hello")));
        }

        if (Advertises(request, "read_file") && MentionsAny(prompt, "read", "show me", "open ", "cat "))
        {
            return new ToolCall(
                id,
                "read_file",
                Arguments(("path", PathLike().Match(prompt) is { Success: true } match ? match.Value : "README.md")));
        }

        if (Advertises(request, "run_shell_command") && MentionsAny(prompt, "build", "test", "run "))
        {
            return new ToolCall(id, "run_shell_command", Arguments(("command", "dotnet build")));
        }

        return null;
    }

    private static bool Advertises(ChatRequest request, string name)
    {
        foreach (var tool in request.Tools)
        {
            if (string.Equals(tool.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MentionsAny(string prompt, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (prompt.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds an argument object, escaping each value with <see cref="JsonEncodedText"/> so a path with
    /// a quote or a backslash still parses.
    /// </summary>
    private static string Arguments(params (string Name, string Value)[] pairs)
    {
        var builder = new StringBuilder("{");
        for (var i = 0; i < pairs.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(pairs[i].Name).Append("\":\"");
            builder.Append(JsonEncodedText.Encode(pairs[i].Value).ToString());
            builder.Append('"');
        }

        return builder.Append('}').ToString();
    }

    private static string Summarize(ChatMessage toolMessage)
    {
        var name = toolMessage.ToolName ?? "the tool";
        var content = toolMessage.Content.Trim();

        if (toolMessage.IsError)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{name} did not succeed: {Shorten(content)}");
        }

        return content.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{name} finished and reported nothing.")
            : string.Create(CultureInfo.InvariantCulture, $"{name} succeeded. It reported: {Shorten(content)}");
    }

    private static IEnumerable<string> Chunk(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(ChunkSize, text.Length - start);
            if (start + length < text.Length)
            {
                // Prefer to break on a space so a delta reads like a word, not a truncation.
                var space = text.LastIndexOf(' ', start + length - 1, length);
                if (space > start)
                {
                    length = space - start + 1;
                }
            }

            yield return text.Substring(start, length);
            start += length;
        }
    }

    private static TokenUsage EstimateUsage(ChatRequest request, string answer)
    {
        // Four characters per token is the usual rough ratio. The demo reports an estimate rather than
        // Unknown so /usage has something to show; a real adapter that hears nothing stays honest.
        var input = 0;
        foreach (var message in request.Messages)
        {
            input += message.Content.Length;
        }

        return new TokenUsage(Math.Max(1, (input / 4) + 1), Math.Max(1, (answer.Length / 4) + 1));
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..157] + "...";

    [GeneratedRegex(@"[A-Za-z0-9_.\-]*[./\\][A-Za-z0-9_.\-/\\]+")]
    private static partial Regex PathLike();
}
