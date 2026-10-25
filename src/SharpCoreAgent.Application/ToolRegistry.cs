using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Application;

/// <summary>
/// The set of tools a turn may call.
/// </summary>
public interface IToolRegistry
{
    /// <summary>Gets the declarations advertised to the model, ordered by name.</summary>
    IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <summary>
    /// Finds a tool by the name the model used.
    /// </summary>
    /// <param name="name">The requested name; matching is ordinal and case-sensitive.</param>
    /// <returns>The tool, or <see langword="null"/> when nothing is registered under that name.</returns>
    ITool? Find(string name);
}

/// <summary>
/// An immutable registry built once from the tools the composition root registered.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;
    private readonly ToolDefinition[] _definitions;

    /// <summary>
    /// Creates a registry.
    /// </summary>
    /// <param name="tools">The tools to serve.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tools"/> is null.</exception>
    /// <exception cref="ArgumentException">Two tools share a name.</exception>
    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        _tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (!_tools.TryAdd(tool.Definition.Name, tool))
            {
                throw new ArgumentException($"Two tools are registered under the name '{tool.Definition.Name}'.", nameof(tools));
            }
        }

        // Sorted so the tool block inside each request is byte-identical between runs, which keeps
        // provider prompt caching effective and golden-file tests stable.
        _definitions = [.. _tools.Values
            .Select(tool => tool.Definition)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> Definitions => _definitions;

    /// <inheritdoc />
    public ITool? Find(string name) => _tools.GetValueOrDefault(name);
}
