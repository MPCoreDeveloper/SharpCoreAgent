using System.Globalization;
using System.Text.Json.Nodes;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The browser round in one file: what a server's image content becomes when there is a workspace to write
/// it into, and what it says when there is not.
/// </summary>
/// <remarks>
/// Every test runs over a real temp workspace and a scripted server, so the assertions are about files on
/// disk and sentences in a <see cref="ToolResult"/> rather than about internals. Each test names the
/// success criterion it answers.
/// </remarks>
public sealed class McpArtefactTests
{
    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
    ];

    [Fact]
    public async Task AnImageEntryIsWrittenAndTheTextNamesIt()
    {
        // SC-001: a screenshot becomes a file in the workspace, and the model is told where.
        await using var harness = new Harness(Content(Image("image/png", Png)));
        var result = await harness.RunAsync(ApprovalMode.Ask);

        Assert.False(result.IsError);
        var file = Assert.Single(harness.Files());
        Assert.Equal(Png, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], (await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken))[..4]);

        var relative = harness.Workspace.ToRelative(file);
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"[image: image/png · 24 base64 characters → {relative} (16 B)]"),
            result.Content);

        // FR-008: the server called this tool read-only, and it still wrote a file, so the effect says so.
        Assert.Equal(ToolSafety.ReadOnly, harness.Tool.Safety);
        var effect = Assert.Single(result.Effects);
        Assert.Equal(ToolEffectKind.Created, effect.Kind);
        Assert.Equal(relative, effect.Target);
    }

    [Fact]
    public async Task AResourceBlobIsSavedByTheSameRule()
    {
        // FR-001: the two saved shapes are an image entry and a resource carrying a blob.
        await using var harness = new Harness(Content(Resource("file:///shot.png", "image/png", Png)));
        var result = await harness.RunAsync(ApprovalMode.Ask);

        var file = Assert.Single(harness.Files());
        Assert.EndsWith(".png", file, StringComparison.Ordinal);
        Assert.Equal(Png, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Assert.Contains("file:///shot.png", result.Content, StringComparison.Ordinal);
        Assert.Contains("→ " + harness.Workspace.ToRelative(file), result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResourceWithTextIsUnchanged()
    {
        // A text resource was already readable, and this round does not touch it.
        await using var harness = new Harness(Content(new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = "file:///a.txt",
                ["mimeType"] = "text/plain",
                ["text"] = "body",
            },
        }));
        var result = await harness.RunAsync(ApprovalMode.Ask);

        Assert.Equal("[resource: file:///a.txt]\nbody", result.Content);
        Assert.Empty(harness.Files());
        Assert.Empty(result.Effects);
    }

    [Fact]
    public async Task ASecondCallAdvancesAndNeverOverwrites()
    {
        // SC-005: the ordinal advances, so the file the transcript already names cannot be replaced.
        await using var harness = new Harness(Content(Image("image/png", Png)));
        var first = await harness.RunAsync(ApprovalMode.Ask);
        var kept = Assert.Single(harness.Files());
        var keptBytes = await File.ReadAllBytesAsync(kept, TestContext.Current.CancellationToken);

        var second = await harness.RunAsync(ApprovalMode.Ask);

        var files = harness.Files();
        Assert.Equal(2, files.Count);
        Assert.EndsWith("-1.png", files[0], StringComparison.Ordinal);
        Assert.EndsWith("-2.png", files[1], StringComparison.Ordinal);
        Assert.NotEqual(first.Content, second.Content);
        Assert.Equal(keptBytes, await File.ReadAllBytesAsync(kept, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadOnlyApprovalWritesNothingAndSaysSo()
    {
        // SC-003, FR-010: the default context may not write, and the refusal is one named reason.
        await using var harness = new Harness(Content(Image("image/png", Png)));
        var result = await harness.RunAsync();

        Assert.False(result.IsError);
        Assert.Empty(harness.Files());
        Assert.False(Directory.Exists(harness.ArtefactFolder));
        Assert.Equal(
            "[image: image/png · 24 base64 characters] · not saved: this session may not write",
            result.Content);
        Assert.Empty(result.Effects);
    }

    [Fact]
    public async Task TheByteCeilingIsCheckedBeforeDecoding()
    {
        // SC-004, FR-006: the estimate refuses an answer before its bytes exist, and one byte under the
        // same ceiling is still saved — the ceiling is a bound, not a target.
        await using var over = new Harness(
            Content(Image("image/png", new byte[1025])),
            new McpClientOptions { MaxArtefactBytes = 1024 });
        var refused = await over.RunAsync(ApprovalMode.Ask);

        Assert.False(refused.IsError);
        Assert.Empty(over.Files());
        Assert.EndsWith("· not saved: larger than 1 KiB", refused.Content, StringComparison.Ordinal);

        await using var under = new Harness(
            Content(Image("image/png", new byte[1024])),
            new McpClientOptions { MaxArtefactBytes = 1024 });
        var saved = await under.RunAsync(ApprovalMode.Ask);

        Assert.Single(under.Files());
        Assert.DoesNotContain("not saved", saved.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePerCallCeilingSavesTheFirstAndDescribesTheRest()
    {
        // FR-006: the count is per call, and the artefacts past it are described rather than dropped.
        await using var harness = new Harness(
            Content(Image("image/png", Png), Image("image/png", Png)),
            new McpClientOptions { MaxArtefactsPerCall = 1 });
        var result = await harness.RunAsync(ApprovalMode.Ask);

        Assert.Single(harness.Files());
        var sentences = result.Content.Split("\n\n");
        Assert.Equal(2, sentences.Length);
        Assert.Contains("→ ", sentences[0], StringComparison.Ordinal);
        Assert.EndsWith("· not saved: the call already saved 1 artefacts", sentences[1], StringComparison.Ordinal);
        Assert.Single(result.Effects);
    }

    [Fact]
    public async Task AnUnknownMimeTypeIsDescribedNotGuessed()
    {
        // FR-004: the table decides the extension, and a type that is not in it is a sentence.
        await using var tiff = new Harness(Content(Image("image/tiff", Png)));
        var refused = await tiff.RunAsync(ApprovalMode.Ask);

        Assert.Empty(tiff.Files());
        Assert.EndsWith("· not saved: image/tiff is not a format this client saves", refused.Content, StringComparison.Ordinal);

        (string Mime, string Extension)[] accepted =
        [
            ("image/png", "png"),
            ("image/jpeg", "jpg"),
            ("image/jpg", "jpg"),
            ("image/gif", "gif"),
            ("image/webp", "webp"),
            ("image/bmp", "bmp"),
        ];

        foreach (var (mime, extension) in accepted)
        {
            await using var saved = new Harness(Content(Image(mime, Png)));
            await saved.RunAsync(ApprovalMode.Ask);
            Assert.EndsWith("." + extension, Assert.Single(saved.Files()), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InvalidBase64IsASentenceNotAnException()
    {
        // SC-004, FR-009: a payload that will not decode costs a sentence, not the call.
        await using var harness = new Harness(Content(Image("image/png", "%%%")));
        var result = await harness.RunAsync(ApprovalMode.Ask);

        Assert.False(result.IsError);
        Assert.Empty(harness.Files());
        Assert.EndsWith("· not saved: the data is not valid base64", result.Content, StringComparison.Ordinal);
        Assert.False(Directory.Exists(harness.ArtefactFolder));
    }

    [Fact]
    public async Task TheSameAnswerInTheSameStateProducesTheSameName()
    {
        // FR-014: nothing in the name depends on the clock, a guid or the phase of the moon.
        await using var first = new Harness(Content(Image("image/png", Png)));
        await using var second = new Harness(Content(Image("image/png", Png)));

        var left = await first.RunAsync(ApprovalMode.Ask);
        var right = await second.RunAsync(ApprovalMode.Ask);

        Assert.Equal(left.Content, right.Content);
        Assert.Equal(
            Path.GetFileName(Assert.Single(first.Files())),
            Path.GetFileName(Assert.Single(second.Files())));
    }

    private static JsonObject Image(string mimeType, byte[] bytes) => new()
    {
        ["type"] = "image",
        ["mimeType"] = mimeType,
        ["data"] = Convert.ToBase64String(bytes),
    };

    private static JsonObject Image(string mimeType, string base64) => new()
    {
        ["type"] = "image",
        ["mimeType"] = mimeType,
        ["data"] = base64,
    };

    private static JsonObject Resource(string uri, string mimeType, byte[] bytes) => new()
    {
        ["type"] = "resource",
        ["resource"] = new JsonObject
        {
            ["uri"] = uri,
            ["mimeType"] = mimeType,
            ["blob"] = Convert.ToBase64String(bytes),
        },
    };

    private static JsonObject Content(params JsonObject[] entries)
    {
        var array = new JsonArray();
        foreach (var entry in entries)
        {
            array.Add(entry);
        }

        return new JsonObject { ["content"] = array };
    }

    /// <summary>
    /// A scripted server, its connection, the adapter over its only tool, and a temp workspace to write into.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private static readonly JsonObject Handshake = new()
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
        };

        private readonly ScriptedMcpServer _server;
        private readonly McpClientOptions _options;
        private readonly string _root;
        private McpTool? _tool;

        public Harness(JsonObject answer, McpClientOptions? options = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "sharpcoreagent-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Workspace = Workspace.Create(_root);
            _options = options ?? new McpClientOptions();

            var content = answer.DeepClone();
            _server = new ScriptedMcpServer(request => request.Method switch
            {
                "initialize" => ScriptedMcpServer.Answer(request.Id, Handshake.DeepClone()),
                "tools/list" => ScriptedMcpServer.Answer(
                    request.Id,
                    new JsonObject { ["tools"] = new JsonArray(ToolJson()) }),
                "tools/call" => ScriptedMcpServer.Answer(request.Id, content.DeepClone()),
                _ => null,
            });

            Connection = new McpServerConnection("npx", "npx -y test-server", _server.Input, _server.Output, _options);
        }

        public McpServerConnection Connection { get; }

        public Workspace Workspace { get; }

        public string ArtefactFolder => Path.Combine(_root, ".sharpcoreagent", "artifacts");

        public McpTool Tool => _tool ?? throw new InvalidOperationException("the tool has not been started");

        /// <summary>Gets the artefacts on disk, ordered by name so an ordinal assertion means something.</summary>
        /// <returns>The full paths, or nothing when the folder was never created.</returns>
        public IReadOnlyList<string> Files() => Directory.Exists(ArtefactFolder)
            ? [.. Directory.GetFiles(ArtefactFolder).OrderBy(path => path, StringComparer.Ordinal)]
            : [];

        /// <summary>Calls the server's tool once.</summary>
        /// <param name="approval">The approval mode the loop would have put in the context.</param>
        /// <returns>What the model would read.</returns>
        public async Task<ToolResult> RunAsync(ApprovalMode approval = ApprovalMode.ReadOnly)
        {
            if (_tool is null)
            {
                await Connection.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
                _tool = new McpTool("npx_browser_take_screenshot", Connection.Tools[0], Connection, _options);
            }

            return await _tool.ExecuteAsync(
                new ToolCall("call-1", "npx_browser_take_screenshot", "{}"),
                new ToolInvocationContext(Workspace) { Approval = approval },
                TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            await _server.DisposeAsync().ConfigureAwait(false);

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leftover litter in the temp directory must not fail an assertion about a sentence.
            }
        }

        private static JsonObject ToolJson() => new()
        {
            ["name"] = "browser_take_screenshot",
            ["description"] = "take a screenshot of the page",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        };
    }
}
