using System.Globalization;
using System.Text;
using System.Text.Json;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Sessions;

namespace SharpCoreAgent.Infrastructure.Sessions;

/// <summary>
/// Writes a transcript as a JSON document under <c>.sharpcoreagent/sessions</c> and reads it back.
/// </summary>
/// <remarks>
/// Two properties matter more than anything else here. The first is that a save is <em>atomic</em>: the
/// document is written to a sibling <c>.tmp</c> file and moved into place, so a crash can leave an old
/// session or a new one but never half of either. The second is that the document is
/// <em>reproducible</em>: property order, indentation and line endings are fixed, so a save → load →
/// save cycle is byte-identical apart from the timestamp.
/// <para>
/// A key, an <c>Authorization</c> header or an environment value has no place in this document and no
/// property to hold one, which is what makes the "grep the file for the key" test cheap to write and
/// impossible to fail silently.
/// </para>
/// </remarks>
public sealed class JsonFileSessionStore : ISessionStore
{
    /// <summary>Gets the version this build writes and the only version it reads.</summary>
    public const int CurrentVersion = 1;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <inheritdoc />
    /// <exception cref="IOException">The file or its directory could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The destination is not writable.</exception>
    public async Task SaveAsync(SessionSnapshot snapshot, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(ToDocument(snapshot), SessionJsonContext.Default.SessionDocument)
            + "\n";

        // The temporary file lives beside the target so the move stays on one volume and stays atomic.
        var temporary = string.Concat(target, ".tmp");
        try
        {
            var bytes = Utf8NoBom.GetBytes(json);
            await using (var stream = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static SessionDocument ToDocument(SessionSnapshot snapshot) => new()
    {
        Version = CurrentVersion,
        WorkspaceRoot = snapshot.WorkspaceRoot,
        Provider = snapshot.Provider,
        ModelId = snapshot.ModelId,
        Approval = snapshot.Approval.ToString(),
        CreatedAtUtc = FormatTimestamp(snapshot.CreatedAtUtc),
        UpdatedAtUtc = FormatTimestamp(snapshot.UpdatedAtUtc),
        Messages = [.. snapshot.Messages.Select(ToDocument)],
        Usage = [.. snapshot.Usage.Select(usage => new SessionUsageDocument
        {
            ModelId = usage.ModelId,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            Turns = usage.Turns,
        })],
    };

    private static SessionMessageDocument ToDocument(ChatMessage message) => new()
    {
        Role = message.Role.ToString(),
        Content = message.Content,
        ToolCalls = message.ToolCalls.Count == 0
            ? null
            : [.. message.ToolCalls.Select(call => new SessionToolCallDocument
            {
                Id = call.Id,
                Name = call.Name,
                ArgumentsJson = call.ArgumentsJson,
            })],
        ToolCallId = message.ToolCallId,
        ToolName = message.ToolName,
        // The flag only carries meaning on a tool message; elsewhere it is noise in every saved file.
        IsError = message.Role == ChatRole.Tool ? message.IsError : null,
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a stray .tmp behind is the lesser problem; the caller is about to hear about the
            // write that failed, and a save error must not become a delete error instead.
        }
    }

    /// <inheritdoc />
    /// <exception cref="SessionFormatException">
    /// The file is missing, is not JSON, or describes a version or value this build does not understand.
    /// </exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    public async Task<SessionSnapshot> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var source = Path.GetFullPath(path);
        string text;
        try
        {
            text = await File.ReadAllTextAsync(source, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SessionFormatException($"session file '{source}' does not exist.", ex);
        }

        SessionDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(text, SessionJsonContext.Default.SessionDocument);
        }
        catch (JsonException ex)
        {
            throw new SessionFormatException($"session file '{source}' is not valid session JSON: {ex.Message}", ex);
        }

        if (document is null)
        {
            throw new SessionFormatException($"session file '{source}' is empty.");
        }

        if (document.Version != CurrentVersion)
        {
            throw new SessionFormatException(
                $"session file '{source}' is version {document.Version}; this build reads version {CurrentVersion}.");
        }

        return new SessionSnapshot(
            document.WorkspaceRoot,
            document.Provider,
            document.ModelId,
            ParseApproval(source, document.Approval),
            ParseTimestamp(source, "createdAtUtc", document.CreatedAtUtc),
            ParseTimestamp(source, "updatedAtUtc", document.UpdatedAtUtc),
            [.. document.Messages.Select(message => ToMessage(source, message))],
            [.. document.Usage.Select(usage => ToUsage(source, usage))]);
    }

    private static ChatMessage ToMessage(string source, SessionMessageDocument message)
    {
        var role = ParseRole(source, message.Role);

        if (role == ChatRole.Tool &&
            (string.IsNullOrWhiteSpace(message.ToolCallId) || string.IsNullOrWhiteSpace(message.ToolName)))
        {
            throw new SessionFormatException(
                $"session file '{source}' has a tool message without a toolCallId and toolName, which cannot be replayed.");
        }

        return new ChatMessage
        {
            Role = role,
            Content = message.Content ?? string.Empty,
            ToolCalls = message.ToolCalls is null
                ? []
                : [.. message.ToolCalls.Select(call => new ToolCall(call.Id, call.Name, call.ArgumentsJson))],
            ToolCallId = message.ToolCallId,
            ToolName = message.ToolName,
            IsError = message.IsError ?? false,
        };
    }

    private static ModelUsage ToUsage(string source, SessionUsageDocument usage)
    {
        if (string.IsNullOrWhiteSpace(usage.ModelId))
        {
            throw new SessionFormatException($"session file '{source}' has a usage entry without a modelId.");
        }

        return new ModelUsage(usage.ModelId, usage.InputTokens, usage.OutputTokens, usage.Turns);
    }

    private static ChatRole ParseRole(string source, string? text)
    {
        if (TryParseEnumName<ChatRole>(text, out var role))
        {
            return role;
        }

        throw new SessionFormatException(
            $"session file '{source}' has message role '{text}'; expected one of {string.Join(", ", Enum.GetNames<ChatRole>())}.");
    }

    private static ApprovalMode ParseApproval(string source, string? text)
    {
        if (TryParseEnumName<ApprovalMode>(text, out var approval))
        {
            return approval;
        }

        throw new SessionFormatException(
            $"session file '{source}' has approval mode '{text}'; expected one of {string.Join(", ", Enum.GetNames<ApprovalMode>())}.");
    }

    private static bool TryParseEnumName<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // A numeric string would bind to the underlying value and let a corrupted file pick a mode by
        // accident, so names only.
        var trimmed = text.Trim();
        if (char.IsDigit(trimmed[0]) || trimmed[0] == '-')
        {
            return false;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    private static DateTimeOffset ParseTimestamp(string source, string property, string? text)
    {
        if (DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out var value))
        {
            return value;
        }

        throw new SessionFormatException(
            $"session file '{source}' has '{property}' = '{text}', which is not an ISO-8601 timestamp.");
    }
}
