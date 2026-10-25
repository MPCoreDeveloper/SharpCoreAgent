using System.Text;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Sessions;
using Xunit;

namespace SharpCoreAgent.Tests.Sessions;

/// <summary>
/// The session file is the only durable artifact the tool produces, so the tests pin the two promises
/// that make it trustworthy: it round-trips exactly, and it never contains a credential.
/// </summary>
public sealed class SessionStoreTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 23, 10, 15, 2, 184, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 10, 23, 10, 16, 41, 2, TimeSpan.Zero);

    [Fact]
    public async Task SaveAndLoadRoundTripTheTranscriptAndUsage()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, ".sharpcoreagent", "sessions", "session.json");
        var store = new JsonFileSessionStore();
        var snapshot = Snapshot(directory.Path);

        await store.SaveAsync(snapshot, path, CancellationToken.None);
        var loaded = await store.LoadAsync(path, CancellationToken.None);

        Assert.Equal(snapshot.WorkspaceRoot, loaded.WorkspaceRoot);
        Assert.Equal("deepseek", loaded.Provider);
        Assert.Equal("deepseek-chat", loaded.ModelId);
        Assert.Equal(ApprovalMode.Ask, loaded.Approval);
        Assert.Equal(Created, loaded.CreatedAtUtc);
        Assert.Equal(Updated, loaded.UpdatedAtUtc);

        Assert.Equal(5, loaded.Messages.Count);
        Assert.Equal(ChatRole.System, loaded.Messages[0].Role);
        Assert.Equal(ChatRole.User, loaded.Messages[1].Role);
        Assert.Equal("add a README section", loaded.Messages[1].Content);

        var assistant = loaded.Messages[2];
        Assert.Equal(ChatRole.Assistant, assistant.Role);
        Assert.Empty(assistant.Content);
        var call = Assert.Single(assistant.ToolCalls);
        Assert.Equal(new ToolCall("call_0", "read_file", """{"path":"README.md"}"""), call);

        var tool = loaded.Messages[3];
        Assert.Equal(ChatRole.Tool, tool.Role);
        Assert.Equal("call_0", tool.ToolCallId);
        Assert.Equal("read_file", tool.ToolName);
        Assert.False(tool.IsError);
        Assert.Equal("  1│# SharpCoreAgent", tool.Content);

        Assert.Equal(ChatRole.Assistant, loaded.Messages[4].Role);
        Assert.Equal("I added the section.", loaded.Messages[4].Content);

        Assert.Equal(2, loaded.Usage.Count);
        Assert.Equal(new ModelUsage("deepseek:deepseek-chat", 1842, 311, 2), loaded.Usage[0]);
        // Unknown usage must survive the round trip as unknown, not as zero.
        Assert.Equal(new ModelUsage("grok:grok-4", null, null, 1), loaded.Usage[1]);
        Assert.Null(loaded.Usage[1].InputTokens);
    }

    [Fact]
    public async Task SavedDocumentMatchesTheContract()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "session.json");
        await new JsonFileSessionStore().SaveAsync(Snapshot(directory.Path), path, CancellationToken.None);

        var text = await File.ReadAllTextAsync(path, CancellationToken.None);

        Assert.StartsWith("{\n  \"version\": 1,\n  \"workspaceRoot\": ", text, StringComparison.Ordinal);
        Assert.Contains("\n  \"approval\": \"Ask\",\n", text, StringComparison.Ordinal);
        Assert.Contains("\"inputTokens\": null", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);

        var bytes = await File.ReadAllBytesAsync(path, CancellationToken.None);
        Assert.False(bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.False(File.Exists(string.Concat(path, ".tmp")));
    }

    [Fact]
    public async Task ResavingAnUnchangedSessionOnlyMovesTheTimestamp()
    {
        using var directory = TempDirectory.Create();
        var store = new JsonFileSessionStore();
        var first = Path.Combine(directory.Path, "first.json");
        var second = Path.Combine(directory.Path, "second.json");

        await store.SaveAsync(Snapshot(directory.Path), first, CancellationToken.None);
        var loaded = await store.LoadAsync(first, CancellationToken.None);
        var moved = new DateTimeOffset(2026, 10, 23, 11, 0, 0, 500, TimeSpan.Zero);
        await store.SaveAsync(loaded with { UpdatedAtUtc = moved }, second, CancellationToken.None);

        var firstLines = (await File.ReadAllTextAsync(first, CancellationToken.None)).Split('\n');
        var secondLines = (await File.ReadAllTextAsync(second, CancellationToken.None)).Split('\n');

        Assert.Equal(firstLines.Length, secondLines.Length);
        var differences = firstLines
            .Zip(secondLines)
            .Where(pair => !string.Equals(pair.First, pair.Second, StringComparison.Ordinal))
            .ToList();
        var difference = Assert.Single(differences);
        Assert.Contains("updatedAtUtc", difference.First, StringComparison.Ordinal);
        Assert.Contains("11:00:00.500Z", difference.Second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAnUnknownVersionAndNamesTheFile()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "future.json");
        await File.WriteAllTextAsync(
            path,
            """{"version":2,"workspaceRoot":".","provider":"demo","modelId":"demo","approval":"Ask","createdAtUtc":"2026-10-23T10:15:02.184Z","updatedAtUtc":"2026-10-23T10:15:02.184Z","messages":[],"usage":[]}""",
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<SessionFormatException>(
            () => new JsonFileSessionStore().LoadAsync(path, CancellationToken.None));

        Assert.Contains("future.json", exception.Message, StringComparison.Ordinal);
        Assert.Contains("version 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesMalformedAndImpossibleDocuments()
    {
        using var directory = TempDirectory.Create();
        var store = new JsonFileSessionStore();
        var malformed = Path.Combine(directory.Path, "malformed.json");
        var badRole = Path.Combine(directory.Path, "bad-role.json");
        await File.WriteAllTextAsync(malformed, "{ not json", CancellationToken.None);
        await File.WriteAllTextAsync(
            badRole,
            """{"version":1,"workspaceRoot":".","provider":"demo","modelId":"demo","approval":"Ask","createdAtUtc":"2026-10-23T10:15:02.184Z","updatedAtUtc":"2026-10-23T10:15:02.184Z","messages":[{"role":"Wizard","content":"hi"}],"usage":[]}""",
            CancellationToken.None);

        await Assert.ThrowsAsync<SessionFormatException>(() => store.LoadAsync(malformed, CancellationToken.None));
        var roleFailure = await Assert.ThrowsAsync<SessionFormatException>(
            () => store.LoadAsync(badRole, CancellationToken.None));
        Assert.Contains("Wizard", roleFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFileIsAFormatError()
    {
        using var directory = TempDirectory.Create();

        var exception = await Assert.ThrowsAsync<SessionFormatException>(() =>
            new JsonFileSessionStore().LoadAsync(Path.Combine(directory.Path, "absent.json"), CancellationToken.None));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeverWritesAConfiguredCredential()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "session.json");
        const string Name = "DEEPSEEK_API_KEY";
        const string Value = "sk-never-in-a-transcript";
        var previous = Environment.GetEnvironmentVariable(Name);

        Environment.SetEnvironmentVariable(Name, Value);
        try
        {
            await new JsonFileSessionStore().SaveAsync(Snapshot(directory.Path), path, CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(path, CancellationToken.None);
            var text = Encoding.UTF8.GetString(bytes);

            Assert.DoesNotContain(Value, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, previous);
        }
    }

    private static SessionSnapshot Snapshot(string workspace) => new(
        workspace,
        "deepseek",
        "deepseek-chat",
        ApprovalMode.Ask,
        Created,
        Updated,
        [
            ChatMessage.System("You are SharpCoreAgent."),
            ChatMessage.User("add a README section"),
            ChatMessage.Assistant(string.Empty, [new ToolCall("call_0", "read_file", """{"path":"README.md"}""")]),
            ChatMessage.FromToolResult(ToolResult.Success("call_0", "read_file", "  1│# SharpCoreAgent")),
            ChatMessage.Assistant("I added the section."),
        ],
        [
            new ModelUsage("deepseek:deepseek-chat", 1842, 311, 2),
            new ModelUsage("grok:grok-4", null, null, 1),
        ]);

    /// <summary>A unique directory under the temp path, removed when the test ends.</summary>
    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sharpcoreagent-sessions",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a passing test over.
            }
        }
    }
}
