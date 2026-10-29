using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpCoreAgent.Infrastructure.McpServer;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// stdout carries protocol messages and nothing else. These assertions are the framing a client depends on:
/// one line per message, UTF-8 with no byte-order mark, and a refusal instead of a split when a payload carries
/// a line break of its own.
/// </summary>
public sealed class McpServerStdoutTests
{
    [Fact]
    public async Task AnAnswerIsOneLineOfUtf8WithoutAByteOrderMark()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"ping"}""" + "\n"));
        using var output = new MemoryStream();

        await using (var session = new McpServerSession(input, output, new McpServerOptions()))
        {
            await session.RunAsync(TestContext.Current.CancellationToken);
        }

        var bytes = output.ToArray();
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(1, bytes.Count(value => value == (byte)'\n'));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
    }

    [Fact]
    public async Task TwoMessagesAreTwoLinesInOrder()
    {
        using var output = new MemoryStream();
        await using var writer = new StdioRpcWriter(output);

        await writer.WriteAsync(new JsonObject { ["id"] = 1 }, Token);
        await writer.WriteAsync(new JsonObject { ["id"] = 2 }, Token);

        var lines = Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Contains("\"id\":1", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"id\":2", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMultiLineToolResultStillTravelsAsOneLine()
    {
        using var output = new MemoryStream();
        await using var writer = new StdioRpcWriter(output);
        var message = new JsonObject
        {
            ["result"] = new JsonObject { ["text"] = "src/Program.cs:1-2\none\ntwo\r\nthree" },
        };

        await writer.WriteAsync(message, Token);

        var bytes = output.ToArray();
        Assert.Equal(1, bytes.Count(value => value == (byte)'\n'));
        Assert.DoesNotContain((byte)'\r', bytes);

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        Assert.Equal(
            "src/Program.cs:1-2\none\ntwo\r\nthree",
            document.RootElement.GetProperty("result").GetProperty("text").GetString());
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
