using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.Application;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The delegation tool is only reachable if the container can hand it the registry it belongs to, so these tests
/// assert the container rather than the tool.
/// </summary>
/// <remarks>
/// Probe <c>F-1</c> was a startup crash, not a preference: a <c>TaskTool</c> that took <c>IEnumerable&lt;ITool&gt;</c>
/// could not be part of the set it was handed, and every host failed before its first turn. The first test is that
/// regression. The second is the size no earlier round wrote down — <c>specs/007-external-roots/tasks.md</c> records
/// that <c>ToolRegistryTests</c> pins order, duplicates and lookup but never a count, so the remark in
/// <c>ServiceCollectionExtensions</c> was the only place the number lived until now.
/// </remarks>
public sealed class TaskToolRegistrationTests
{
    /// <summary>The tools a default build advertises, sorted, with no flag set.</summary>
    private static readonly string[] DefaultSurface =
    [
        "code_context",
        "delete_file",
        "dotnet_verify",
        "file_diagnostics",
        "find_references",
        "find_symbol",
        "git_diff",
        "git_status",
        "index_status",
        "list_artifacts",
        "list_directory",
        "list_roots",
        "node_verify",
        "python_verify",
        "read_file",
        "replace_in_file",
        "run_shell",
        "search_code",
        "search_text",
        "shell_output",
        "shell_stop",
        "signature_at",
        "task",
        "uses_at",
        "view_image",
        "write_file",
    ];

    [Fact]
    public void TheDelegatorIsRegisteredInABuildThatConfiguredNothing()
    {
        using var provider = Build(new SharpCoreAgentOptions());

        var registry = provider.GetRequiredService<IToolRegistry>();
        var tool = registry.Find(TaskTool.Name);

        Assert.NotNull(tool);
        Assert.Equal(ToolSafety.Destructive, tool.Safety);
        Assert.Contains(registry.Definitions, definition => definition.Name == TaskTool.Name);
    }

    [Fact]
    public void EveryToolResolvesAndTheSurfaceIsTheDocumentedSet()
    {
        // Both halves of the promise at once: the registry builds without a circular dependency, and the set is the
        // twenty-five the container's own remark names. A tool arriving or leaving has to move this line with it.
        using var provider = Build(new SharpCoreAgentOptions());

        var tools = provider.GetServices<ITool>().ToList();
        var names = tools.Select(tool => tool.Definition.Name).Order(StringComparer.Ordinal);

        Assert.Equal(DefaultSurface, names);
        Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Definition.Description)));
        Assert.Single(tools.OfType<TaskTool>());
    }

    [Fact]
    public void TheToolsAreSingletonsSoOneDelegatorServesTheProcess()
    {
        // A second TaskTool would be a second reader of the registry and a second set of narration lines; the tool holds
        // no state, so the instance the registry advertises is the instance a turn gets.
        using var provider = Build(new SharpCoreAgentOptions());

        var first = provider.GetServices<ITool>().OfType<TaskTool>().Single();
        var second = provider.GetServices<ITool>().OfType<TaskTool>().Single();

        Assert.Same(first, second);
        Assert.Same(first, provider.GetRequiredService<IToolRegistry>().Find(TaskTool.Name));
    }

    private static ServiceProvider Build(SharpCoreAgentOptions options) =>
        new ServiceCollection().AddSharpCoreAgent(options).BuildServiceProvider();
}
