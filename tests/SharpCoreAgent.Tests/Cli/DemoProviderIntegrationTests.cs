using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Infrastructure.Credentials;
using SharpCoreAgent.Infrastructure.Providers;
using SharpCoreAgent.Infrastructure.Sessions;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The composition root and the agent, exercised end to end on the offline provider.
/// </summary>
/// <remarks>
/// These tests start where the process starts -- a parsed command line -- and stop where it stops -- a
/// written transcript. Nothing here touches the network, so the suite stays offline and fast while still
/// covering the wiring that in-process unit tests of each part cannot: option projection, tool
/// registration, the approval gate and the session store.
/// </remarks>
public sealed class DemoProviderIntegrationTests
{
    [Fact]
    public async Task ACommandLineRunsATurnWithAToolAndWritesTheTranscript()
    {
        using var directory = TempDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "README.md"), "# Demo", TestContext.Current.CancellationToken);

        var cli = CliOptions.Parse(
            ["run", "list the files", "--provider", "demo", "--workspace", directory.Path, "--approval", "allow"],
            NoEnvironment);

        using var provider = CliHost.Build(cli);
        var model = provider.GetRequiredService<ModelDescriptor>();
        Assert.Equal("demo:demo-scripted", model.QualifiedName);
        Assert.True(CliHost.TryEnsureCredentials(
            model, provider.GetRequiredService<ICredentialStore>(), TextWriter.Null));

        var options = provider.GetRequiredService<AgentOptions>();
        var agent = provider.GetRequiredService<CodingAgent>();
        var observer = new RecordingObserver();
        var session = new AgentSession(Workspace.Create(directory.Path), model, cli.Approval, options.SystemPrompt);

        var report = await agent.RunTurnAsync(session, cli.Prompt, observer, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, report.Outcome);
        Assert.Equal("demo:demo-scripted", report.ModelId);
        Assert.Contains(observer.Events.OfType<ToolRequested>(), e => e.ToolName == "list_directory");
        Assert.Contains(observer.Events.OfType<ToolFinished>(), e => !e.IsError);
        Assert.Contains(observer.Events.OfType<AgentTextDelta>(), e => e.Text.Length > 0);
        Assert.True(report.Usage.InputTokens > 0);

        var path = CliOptions.DefaultSessionPath(directory.Path, session.CreatedAtUtc);
        Assert.True(await CliHost.TrySaveSessionAsync(
            provider.GetRequiredService<ISessionStore>(), session, path, TextWriter.Null));

        var snapshot = await new JsonFileSessionStore().LoadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("demo", snapshot.Provider);
        Assert.Equal("demo-scripted", snapshot.ModelId);
        Assert.Equal(directory.Path, snapshot.WorkspaceRoot);
        Assert.Equal(ChatRole.User, snapshot.Messages[1].Role);
        Assert.Equal("list the files", snapshot.Messages[1].Content);
        Assert.Single(snapshot.Usage);
    }
    [Fact]
    public async Task ReadOnlyApprovalRefusesAWriteAndLeavesTheFileAlone()
    {
        using var directory = TempDirectory.Create();
        var notes = Path.Combine(directory.Path, "notes.txt");
        await File.WriteAllTextAsync(notes, "original", TestContext.Current.CancellationToken);

        var cli = CliOptions.Parse(
            [
                "run", "write hello to notes.txt", "--provider", "demo",
                "--workspace", directory.Path, "--approval", "readonly",
            ],
            NoEnvironment);

        using var provider = CliHost.Build(cli);
        var model = provider.GetRequiredService<ModelDescriptor>();
        var options = provider.GetRequiredService<AgentOptions>();
        var agent = provider.GetRequiredService<CodingAgent>();
        var session = new AgentSession(Workspace.Create(directory.Path), model, cli.Approval, options.SystemPrompt);

        var report = await agent.RunTurnAsync(session, cli.Prompt, null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, report.Outcome);
        var refusal = Assert.Single(report.ToolResults);
        Assert.Equal("write_file", refusal.ToolName);
        Assert.True(refusal.IsRefused);
        Assert.Contains("ReadOnly", refusal.Content, StringComparison.Ordinal);
        Assert.Equal("original", await File.ReadAllTextAsync(notes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AMissingCredentialIsReportedWithTheVariableName()
    {
        var descriptor = ModelCatalog.DescriptorFor("deepseek", null);
        var credentials = new EnvironmentCredentialStore(readEnvironment: _ => null);
        using var error = new StringWriter();

        Assert.False(CliHost.TryEnsureCredentials(descriptor, credentials, error));
        Assert.Contains("DEEPSEEK_API_KEY", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownWorkspaceIsAUsageErrorWithAReason()
    {
        var cli = CliOptions.Parse(["run", "hi", "--workspace", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], NoEnvironment);
        using var error = new StringWriter();

        Assert.False(CliHost.TryOpenWorkspace(cli, error, out var workspace));
        Assert.Null(workspace);
        Assert.Contains("does not exist", error.ToString(), StringComparison.Ordinal);
    }
    private static string? NoEnvironment(string name) => null;

    /// <summary>Records every event the loop reports, in order.</summary>
    private sealed class RecordingObserver : IAgentObserver
    {
        private readonly List<AgentEvent> _events = [];

        public IReadOnlyList<AgentEvent> Events => _events;

        public ValueTask OnEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
        {
            _events.Add(agentEvent);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A unique directory under the temp path, removed when the test ends.</summary>
    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sharpcoreagent-cli",
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
