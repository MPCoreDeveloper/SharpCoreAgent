using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// A minimal MCP server over stdio: one JSON object per line in, one per line out, two tools.
//
// The pid on stderr is not decoration. It is the only thing the process test can use to prove that disposing the
// connection killed this process rather than merely closing its input, and it is written to stderr because that is
// the channel a client may read without parsing it.

var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
var exposeCrash = args.Contains("--crash-tool", StringComparer.Ordinal);
var exposeCatalog = args.Contains("--catalog", StringComparer.Ordinal);

Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"pid={Environment.ProcessId}"));
Console.Error.WriteLine("test-mcp-server ready");
Console.Error.Flush();

while (await input.ReadLineAsync(CancellationToken.None) is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonObject? answer;
    using (var document = JsonDocument.Parse(line))
    {
        answer = Answer(document.RootElement, exposeCrash, exposeCatalog);
    }

    if (answer is not null)
    {
        await output.WriteLineAsync(answer.ToJsonString());
    }
}

static JsonObject? Answer(JsonElement request, bool exposeCrash, bool exposeCatalog)
{
    if (!request.TryGetProperty("method", out var methodElement) || methodElement.GetString() is not { } method)
    {
        return null;
    }

    // No id means a notification: the client is telling us something, and the protocol says not to answer.
    var hasId = request.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number;
    var id = hasId ? idElement.GetInt64() : 0;
    var parameters = request.TryGetProperty("params", out var paramsElement) ? paramsElement : default;

    switch (method)
    {
        case "initialize":
            return Result(id, new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = exposeCatalog
                    ? new JsonObject
                    {
                        ["tools"] = new JsonObject(),
                        ["resources"] = new JsonObject(),
                        ["prompts"] = new JsonObject(),
                    }
                    : new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "test-mcp-server", ["version"] = "1.0.0" },
            });

        case "tools/list":
            var tools = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "echo",
                    ["description"] = "Echo the text back, so a caller can prove the arguments arrived.",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
                        ["required"] = new JsonArray("text"),
                    },
                    ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
                },
                new JsonObject
                {
                    ["name"] = "fail",
                    ["description"] = "Always fails, so a caller can prove an error is reported as one.",
                    ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                },
            };

            if (exposeCrash)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = "crash",
                    ["description"] = "Exits the server after recording one invocation, so a caller can prove a call is not replayed.",
                    ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                });
            }

            return Result(id, new JsonObject { ["tools"] = tools });

        case "tools/call":
            return Call(id, parameters, exposeCrash);

        case "resources/list" when exposeCatalog:
            return Result(id, new JsonObject
            {
                ["resources"] = new JsonArray(new JsonObject
                {
                    ["uri"] = "file:///test.txt",
                    ["name"] = "test.txt",
                    ["mimeType"] = "text/plain",
                    ["description"] = "A test resource",
                }),
            });

        case "resources/read" when exposeCatalog:
            return Result(id, new JsonObject
            {
                ["contents"] = new JsonArray(new JsonObject
                {
                    ["uri"] = "file:///test.txt",
                    ["mimeType"] = "text/plain",
                    ["text"] = "catalog resource",
                }),
            });

        case "prompts/list" when exposeCatalog:
            return Result(id, new JsonObject
            {
                ["prompts"] = new JsonArray(new JsonObject
                {
                    ["name"] = "review",
                    ["description"] = "Review a path",
                }),
            });

        case "prompts/get" when exposeCatalog:
            return Result(id, new JsonObject
            {
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject { ["type"] = "text", ["text"] = "catalog prompt" },
                }),
            });

        default:
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"method '{method}' is not part of this test server" },
            };
    }
}

static JsonObject Call(long id, JsonElement parameters, bool exposeCrash)
{
    var name = parameters.ValueKind == JsonValueKind.Object &&
               parameters.TryGetProperty("name", out var nameElement)
        ? nameElement.GetString()
        : null;

    if (name == "crash" && exposeCrash)
    {
        Console.Error.WriteLine("crash requested");
        Console.Error.Flush();
        Environment.Exit(17);
    }

    if (name == "fail")
    {
        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(Text("this tool always fails")),
            ["isError"] = true,
        });
    }

    if (name != "echo")
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = -32602, ["message"] = $"unknown tool '{name}'" },
        };
    }

    var text = parameters.TryGetProperty("arguments", out var arguments) &&
               arguments.ValueKind == JsonValueKind.Object &&
               arguments.TryGetProperty("text", out var textElement) &&
               textElement.ValueKind == JsonValueKind.String
        ? textElement.GetString() ?? string.Empty
        : string.Empty;

    return Result(id, new JsonObject { ["content"] = new JsonArray(Text(text)) });
}

static JsonObject Result(long id, JsonObject result) => new()
{
    ["jsonrpc"] = "2.0",
    ["id"] = id,
    ["result"] = result,
};

static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };
