using System.Text.Json;
using SharpCoreAgent.Evaluation;

namespace SharpCoreAgent.Tests.Evaluation;

/// <summary>Creates one small, self-contained scenario whose real tools need only the installed .NET SDK.</summary>
internal sealed class EvaluationTestScenario : IDisposable
{
    private EvaluationTestScenario(string root)
    {
        Root = root;
        Directory.CreateDirectory(Path.Combine(root, "workspace"));
        File.WriteAllText(Path.Combine(root, "workspace", "value.txt"), "old\n");
    }

    public string Root { get; }

    public EvaluationScenario Value { get; private set; } = null!;

    public static EvaluationTestScenario Create()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sharpcoreagent-evaluation-tests",
            Guid.NewGuid().ToString("N"),
            "sample");
        var fixture = new EvaluationTestScenario(root);
        fixture.Value = new EvaluationScenario
        {
            SchemaVersion = 1,
            Id = "sample",
            Language = "text",
            Prompt = "Replace old with new and verify the change.",
            Directory = root,
            Turns =
            [
                Call("read_file", """{"path":"value.txt"}"""),
                Call("replace_in_file", """{"path":"value.txt","find":"old","replace":"new"}"""),
                Call("run_shell", """{"command":"dotnet --version","timeout_seconds":30}"""),
                new RecordedTurn { Text = "Changed value.txt and verified it." },
            ],
            AllowedChanges = ["value.txt"],
            Verification = new ScenarioVerification { Tool = "run_shell", Command = "dotnet --version" },
            Grade = new ScenarioGrade
            {
                RuntimeProbe = "dotnet --version",
                Command = "dotnet --version",
                ExpectedFiles = [new ExpectedFile { Path = "value.txt", Contains = ["new"] }],
            },
        };
        return fixture;
    }

    public static RecordedTurn Call(string name, string arguments) => new()
    {
        Calls =
        [
            new RecordedCall
            {
                Name = name,
                Arguments = ParseArguments(arguments),
            },
        ],
    };

    private static JsonElement ParseArguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        var root = Directory.GetParent(Root)?.FullName;
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
