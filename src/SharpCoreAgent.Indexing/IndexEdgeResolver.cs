using SharpCoreDB.Interfaces;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// Turns the names an extractor writes into the symbol ids the graph stores.
/// </summary>
/// <remarks>
/// Extraction cannot know ids: a callee may live in a file the build has not reached, or in a package that is
/// not in the workspace at all. The build therefore resolves once, after every file's declarations are stored,
/// and drops what it cannot resolve — a dangling edge is worse than a missing one because traversal would
/// follow it into nothing.
/// <para>
/// Three lookups, tried in order: the exact key (which is what an extractor emits for a source), the qualified
/// name (what a human or a model types), and the short name. When two declarations share a name the lowest row
/// id wins, so two builds of the same tree resolve the same way.
/// </para>
/// </remarks>
internal sealed class IndexEdgeResolver
{
    private readonly Dictionary<string, long> _byKey;
    private readonly Dictionary<string, long> _byQualifiedName;
    private readonly Dictionary<string, long> _byName;

    private IndexEdgeResolver(
        Dictionary<string, long> byKey,
        Dictionary<string, long> byQualifiedName,
        Dictionary<string, long> byName)
    {
        _byKey = byKey;
        _byQualifiedName = byQualifiedName;
        _byName = byName;
    }

    /// <summary>
    /// Loads the symbol table of one index.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <returns>The resolver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    public static IndexEdgeResolver Load(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var byKey = new Dictionary<string, long>(StringComparer.Ordinal);
        var byQualifiedName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var rows = IndexSql.Query(database, "SELECT id, key, name, qualified_name FROM symbols ORDER BY id");
        foreach (var row in rows)
        {
            var id = IndexSql.AsLong(row.GetValueOrDefault("id"));
            Add(byKey, IndexSql.AsText(row.GetValueOrDefault("key")), id);
            Add(byQualifiedName, IndexSql.AsText(row.GetValueOrDefault("qualified_name")), id);
            Add(byName, IndexSql.AsText(row.GetValueOrDefault("name")), id);
        }

        return new IndexEdgeResolver(byKey, byQualifiedName, byName);
    }

    /// <summary>
    /// Resolves a relation's source, which is always a symbol key.
    /// </summary>
    /// <param name="key">The key the extractor wrote.</param>
    /// <param name="id">The stored symbol id when the key exists.</param>
    /// <returns><see langword="true"/> when the source is indexed.</returns>
    public bool TryResolveKey(string key, out long id) => _byKey.TryGetValue(key, out id);

    /// <summary>
    /// Resolves a relation's target, which is written as a name.
    /// </summary>
    /// <param name="target">The name the extractor saw: a key, a qualified name or a short name.</param>
    /// <param name="id">The stored symbol id when the name resolves.</param>
    /// <returns><see langword="true"/> when the target is indexed.</returns>
    public bool TryResolve(string target, out long id)
    {
        if (_byKey.TryGetValue(target, out id))
        {
            return true;
        }

        return _byQualifiedName.TryGetValue(target, out id) || _byName.TryGetValue(target, out id);
    }

    private static void Add(Dictionary<string, long> lookup, string name, long id)
    {
        if (name.Length > 0)
        {
            lookup.TryAdd(name, id);
        }
    }
}
