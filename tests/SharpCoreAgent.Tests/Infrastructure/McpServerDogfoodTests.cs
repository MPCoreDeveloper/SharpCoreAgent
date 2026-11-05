using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Infrastructure.Mcp;
using SharpCoreAgent.Infrastructure.McpServer;
using SharpCoreAgent.Infrastructure.Tools;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The round's own client against the round's own server: one process starts the other, lists what it publishes, asks
/// it the question it exists for, and gets the text the in-process tool would have produced.
/// </summary>
/// <remarks>
/// <para>
/// This is the test that catches the two halves describing different products — a renamed tool on one side of the
/// boundary, a schema the client cannot read, a server that only answers the requests its own unit tests write. The
/// child is the CLI this build produced, copied next to this assembly by the <c>CopyProductCli</c> target, so the
/// launch is the one <c>mcp config</c> prints for another agent rather than a path only a test knows.
/// </para>
/// <para>
/// No key is configured for the child and nothing here opens a socket. That is not asserted again:
/// <see cref="McpServerCompositionTests"/> proves the container <c>mcp serve</c> registers holds no model and no
/// <see cref="HttpClient"/>, which is the only way a key could be spent at all.
/// </para>
/// <para>
/// The index is built here and the child runs with <c>--no-refresh</c>, so the only thing being measured is the wire:
/// a server that could not read the index would fail the evidence assertion below rather than hide behind a build.
/// </para>
/// </remarks>
public sealed class McpServerDogfoodTests : IDisposable
{
    private const string Arguments = """{"query":"convert celsius to fahrenheit","k":3}""";

    private readonly ToolTestWorkspace _sandbox = ToolTestWorkspace.Create();

    /// <summary>Writes the two files the index is built from.</summary>
    public McpServerDogfoodTests()
    {
        _sandbox.Write("src/Thermometer.cs", """
            namespace Demo.Sensors;

            /// Converts one reading between the two scales a household thermometer shows.
            public sealed class Thermometer
            {
                public double Celsius { get; private set; }

                public static Thermometer FromCelsius(double value) => new() { Celsius = value };

                public double Fahrenheit() => (Celsius * 9 / 5) + 32;
            }
            """);

        _sandbox.Write("src/SensorLog.cs", """
            namespace Demo.Sensors;

            /// Keeps one reading per minute and answers the warmest hour of the day.
            public sealed class SensorLog
            {
                private readonly List<(TimeOnly At, double Celsius)> _readings = [];

                public void Add(TimeOnly at, double celsius) => _readings.Add((at, celsius));

                public (TimeOnly Hour, double Celsius) Warmest() =>
                    _readings.Count == 0 ? default : _readings.MaxBy(reading => reading.Celsius);
            }
            """);
    }

    /// <inheritdoc />
    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public async Task OurOwnClientSeesOurOwnToolsUnderThePrefixATurnWouldCall()
    {
        await IndexAsync();
        await using var host = await StartAsync();

        Assert.Equal(
            [
                "sharpcoreagent_code_context",
                "sharpcoreagent_find_references",
                "sharpcoreagent_find_symbol",
                "sharpcoreagent_index_status",
                "sharpcoreagent_search_code",
                "sharpcoreagent_view_image",
            ],
            host.Tools.Select(tool => tool.Definition.Name).Order(StringComparer.Ordinal));

        // The prefix is the executable's name, which is why the snippet names the apphost: started through `dotnet`,
        // every tool would be called `dotnet_<tool>` and the name FR-017 asks for would not exist.
        Assert.All(host.Tools, tool => Assert.StartsWith("sharpcoreagent_", tool.Definition.Name, StringComparison.Ordinal));

        // The ready line is the server's own, on its stderr: the child is `mcp serve` rather than a stand-in that
        // happens to answer the protocol.
        Assert.Contains(host.Servers[0].Stderr, line => line.Contains("mcp serve:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTextOverStdioIsExactlyTheTextTheToolReturnsInProcess()
    {
        await IndexAsync();
        var expected = await InProcessAsync();
        await using var host = await StartAsync();

        var answered = await host.Servers[0].CallAsync(
            SearchCodeTool.Name,
            Payload(),
            TimeSpan.FromSeconds(30),
            Token);

        Assert.False(answered.IsError, answered.Text);

        // SC-002, asserted rather than claimed: the same query against the same index, with nothing added or removed
        // by the hop.
        Assert.Equal(expected, answered.Text);

        // The text is evidence about the fixture, not two identical refusals that would compare equal.
        Assert.Contains("src/Thermometer.cs", expected, StringComparison.Ordinal);

        // And the adapter a turn actually calls adds nothing on top of the server's own text.
        var adapted = await host
            .Tools
            .Single(tool => string.Equals(tool.Definition.Name, "sharpcoreagent_search_code", StringComparison.Ordinal))
            .ExecuteAsync(ToolTestWorkspace.Call("sharpcoreagent_search_code", Arguments), _sandbox.Context, Token);

        Assert.False(adapted.IsError, adapted.Content);
        Assert.Equal(expected, adapted.Content);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Parses the argument object once, so both sides of the comparison send the same bytes.</summary>
    /// <returns>The argument object, owned by the caller.</returns>
    private static JsonElement Payload()
    {
        using var document = JsonDocument.Parse(Arguments);
        return document.RootElement.Clone();
    }

    /// <summary>Builds the index the child is expected to answer from.</summary>
    private async Task IndexAsync()
    {
        await using var maintainer = SharpCoreDbIndexMaintainer.For(_sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromMinutes(2), Token);

        Assert.False(report.Incomplete, "a partial index would make the parity assertion a coin toss");
        Assert.Equal(2, report.Status.Files);
        Assert.Equal(IndexFreshness.Fresh, report.Status.Freshness);
    }

    /// <summary>
    /// Runs <c>search_code</c> in this process through the registration <c>mcp serve</c> builds, so the text the child
    /// is compared against is the product's own rather than a literal written here.
    /// </summary>
    /// <returns>The tool's text.</returns>
    private async Task<string> InProcessAsync()
    {
        var services = new ServiceCollection();
        services.AddMcpServer(_sandbox.Workspace, new McpServerOptions { Refresh = false });

        await using var provider = services.BuildServiceProvider();
        var tool = provider
            .GetServices<ITool>()
            .Single(candidate => string.Equals(candidate.Definition.Name, SearchCodeTool.Name, StringComparison.Ordinal));

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(SearchCodeTool.Name, Arguments),
            _sandbox.Context,
            Token);

        Assert.False(result.IsError, result.Content);
        return result.Content;
    }

    /// <summary>Starts the product's CLI as an MCP server, the way the snippet printed by <c>mcp config</c> would.</summary>
    /// <returns>The started host, which the caller disposes.</returns>
    private async Task<McpToolHost> StartAsync()
    {
        var host = new McpToolHost([CommandLine()], new McpClientOptions());

        try
        {
            await host.StartAsync(Token);
            return host;
        }
        catch
        {
            // A failed start leaves a child behind that the caller's `await using` would never reach.
            await host.DisposeAsync();
            throw;
        }
    }

    private string CommandLine() => CliBinary.Serve(_sandbox.Root);
}
