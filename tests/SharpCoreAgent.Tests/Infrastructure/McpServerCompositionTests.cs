using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Mcp;
using SharpCoreAgent.Infrastructure.McpServer;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The container <c>mcp serve</c> builds: five read-only tools over a leased index, and nothing that could spend a
/// key, open a socket or write a file.
/// </summary>
/// <remarks>
/// Asserted against the container rather than against the flag, because the promise in FR-001 is structural: an
/// agent starts this process dozens of times a day on a machine whose owner may never have typed a key, so the
/// absence of a model and an <see cref="HttpClient"/> has to be a property of the registration and not of a code
/// path nobody took.
/// </remarks>
public sealed class McpServerCompositionTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "sca-mcp-serve-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the empty workspace the container describes.</summary>
    public McpServerCompositionTests() => Directory.CreateDirectory(_workspace);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    [Fact]
    public void NothingThatCouldSpendAKeyOrOpenASocketIsInTheContainer()
    {
        using var provider = Build();

        Assert.Null(provider.GetService<IChatModel>());
        Assert.Null(provider.GetService<IChatModelResolver>());
        Assert.Null(provider.GetService<HttpClient>());

        // And no client either: this process answers a protocol, it does not start programs that speak one.
        Assert.Null(provider.GetService<McpToolHost>());
    }

    [Fact]
    public void TheRegistryHoldsTheFiveEvidenceToolsAndNothingThatWrites()
    {
        using var provider = Build();

        var definitions = provider.GetRequiredService<IToolRegistry>().Definitions;

        Assert.Equal(
            ["code_context", "find_references", "find_symbol", "index_status", "search_code"],
            definitions.Select(definition => definition.Name).Order(StringComparer.Ordinal));

        Assert.All(definitions, definition => Assert.DoesNotContain("shell", definition.Name, StringComparison.Ordinal));
        Assert.All(definitions, definition => Assert.DoesNotContain("write", definition.Name, StringComparison.Ordinal));
        Assert.All(
            provider.GetServices<ITool>(),
            tool => Assert.Equal(ToolSafety.ReadOnly, tool.Safety));
    }

    [Fact]
    public void TheIndexIsReachedThroughALease()
    {
        using var provider = Build();

        // The type is the promise: an ICodeIndex that opens one handle per operation is what keeps an idle server
        // from blocking a build in another process.
        Assert.IsType<LeasedCodeIndex>(provider.GetRequiredService<SharpCoreAgent.Domain.Index.ICodeIndex>());
    }

    [Fact]
    public async Task TheStreamsATestRegistersAreTheOnesTheSessionAnswersOn()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":9,"method":"ping"}""" + '\n'));
        using var output = new MemoryStream();
        var services = new ServiceCollection();

        // What `mcp serve` does with the process's own stdio: the pair is registered before the server, so the
        // session resolves over it. A test registers two memory streams and gets the same session the product builds.
        services.AddSingleton(new McpServerStreams(input, output));
        services.AddMcpServer(Workspace.Create(_workspace), new McpServerOptions { Refresh = false });

        await using var provider = services.BuildServiceProvider();
        await using var session = provider.GetRequiredService<McpServerSession>();

        Assert.Equal(0, await session.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(5, session.PublishedToolCount);

        var answer = Assert.Single(Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("\"id\":9", answer, StringComparison.Ordinal);
        Assert.Contains("\"result\":{}", answer, StringComparison.Ordinal);
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddMcpServer(Workspace.Create(_workspace), new McpServerOptions { Refresh = false });

        return services.BuildServiceProvider();
    }
}
