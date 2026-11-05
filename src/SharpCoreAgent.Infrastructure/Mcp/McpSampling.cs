using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Model;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// Answers a server's <c>sampling/createMessage</c> with the session's current model.
/// </summary>
/// <remarks>
/// The handler is built for one tool call only, so a sampling request cannot outlive the turn that named the server.
/// Only text messages are accepted: an image or audio part would need a provider-specific wire shape and a second
/// approval question. The policy and rate limits live in <see cref="McpSamplingLimiter"/>; this type turns the request
/// into a <see cref="ChatRequest"/> and the model's answer back into the MCP result shape.
/// </remarks>
internal static class McpSampling
{
    /// <summary>
    /// Builds the handler for one connection, or null when sampling is denied or no model was declared.
    /// </summary>
    /// <param name="models">The resolver for the session's model, or null in a host without one.</param>
    /// <param name="descriptor">The model the session is using, or null when the context did not name one.</param>
    /// <param name="connection">The connection whose limiter counts the request.</param>
    /// <param name="options">The policy and output ceiling.</param>
    /// <returns>The handler, or null when the connection keeps the default <c>-32601</c> answer.</returns>
    public static Func<string, JsonElement, CancellationToken, Task<JsonObject?>>? Create(
        IChatModelResolver? models,
        ModelDescriptor? descriptor,
        McpServerConnection connection,
        McpClientOptions options)
    {
        if (models is null || descriptor is null || options.SamplingPolicy == McpSamplingPolicy.Deny)
        {
            return null;
        }

        return (method, parameters, cancellationToken) =>
            HandleAsync(method, parameters, models, descriptor, connection, options, cancellationToken);
    }

    private static async Task<JsonObject?> HandleAsync(
        string method,
        JsonElement parameters,
        IChatModelResolver models,
        ModelDescriptor descriptor,
        McpServerConnection connection,
        McpClientOptions options,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(method, "sampling/createMessage", StringComparison.Ordinal))
        {
            return null;
        }

        if (parameters.ValueKind != JsonValueKind.Object)
        {
            throw new McpSamplingException("sampling/createMessage requires a params object");
        }

        var messages = ReadMessages(parameters);
        var system = ReadSystemPrompt(parameters);
        var inputTokens = EstimateTokens(system) + messages.Sum(message => EstimateTokens(message.Content));
        if (!connection.TryAcquireSampling(inputTokens, out var refusal))
        {
            throw new McpSamplingException(refusal);
        }

        var maxTokens = ReadMaxTokens(parameters, options.MaxSamplingOutputTokensPerCall);
        var temperature = ReadTemperature(parameters);
        var requestMessages = new List<ChatMessage>(messages.Count + (system.Length > 0 ? 1 : 0));
        if (system.Length > 0)
        {
            requestMessages.Add(ChatMessage.System(system));
        }

        requestMessages.AddRange(messages);

        var model = models.Resolve(descriptor);
        var text = new StringBuilder();
        ChatResponse? completed = null;
        await foreach (var streamEvent in model
                           .StreamAsync(new ChatRequest(descriptor, requestMessages, [], temperature, maxTokens), cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            switch (streamEvent)
            {
                case TextDelta delta:
                    text.Append(delta.Text);
                    break;
                case StreamCompleted done:
                    completed = done.Response;
                    break;
            }
        }

        if (completed is null)
        {
            throw new McpSamplingException("the model ended without completing the sampling request");
        }

        return new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonObject
            {
                ["type"] = "text",
                ["text"] = text.ToString(),
            },
            ["model"] = descriptor.Id,
            ["stopReason"] = StopReason(completed.FinishReason),
        };
    }
 
    private static List<ChatMessage> ReadMessages(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array ||
            messages.GetArrayLength() == 0)
        {
            throw new McpSamplingException("sampling/createMessage requires a non-empty messages array");
        }

        var result = new List<ChatMessage>();
        foreach (var entry in messages.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("role", out var roleElement) ||
                roleElement.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("content", out var content))
            {
                throw new McpSamplingException("each sampling message needs a role and content");
            }

            var role = roleElement.GetString();
            var text = ReadContent(content);
            result.Add(role switch
            {
                "user" => ChatMessage.User(text),
                "assistant" => ChatMessage.Assistant(text),
                _ => throw new McpSamplingException($"sampling role '{role}' is not user or assistant"),
            });
        }

        return result;
    }

    private static string ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            return ReadTextPart(content);
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    throw new McpSamplingException("sampling content parts must be objects");
                }

                builder.Append(ReadTextPart(part));
            }

            return builder.ToString();
        }

        throw new McpSamplingException("sampling content must be text, an object or an array of text parts");
    }

    private static string ReadTextPart(JsonElement part)
    {
        if (!part.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "text", StringComparison.Ordinal))
        {
            throw new McpSamplingException("only text sampling content is supported");
        }

        return part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? string.Empty
            : throw new McpSamplingException("a text sampling part needs its text");
    }

    private static string ReadSystemPrompt(JsonElement parameters) =>
        parameters.TryGetProperty("systemPrompt", out var system) && system.ValueKind == JsonValueKind.String
            ? system.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadMaxTokens(JsonElement parameters, int ceiling)
    {
        if (!parameters.TryGetProperty("maxTokens", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return ceiling;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var requested) || requested < 1)
        {
            throw new McpSamplingException("maxTokens must be a positive integer");
        }

        return Math.Min(requested, ceiling);
    }

    private static double ReadTemperature(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("temperature", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return 0.2;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var temperature) ||
            temperature is < 0 or > 2)
        {
            throw new McpSamplingException("temperature must be a number between 0 and 2");
        }

        return temperature;
    }

    private static string StopReason(FinishReason reason) => reason switch
    {
        FinishReason.Stop => "endTurn",
        FinishReason.Length => "maxTokens",
        FinishReason.ContentFilter => "stopSequence",
        _ => "endTurn",
    };

    private static int EstimateTokens(string text) =>
        text.Length == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(text.Length / 4d));
}

