using System.Globalization;
using SharpCoreDB.Interfaces;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// The one place a statement is handed to the engine, so parameter binding and value reading look the same
/// everywhere in this assembly.
/// </summary>
/// <remarks>
/// Two facts about SharpCoreDB 2.1.1 shape this: every overload takes <c>Dictionary&lt;string, object&gt;</c>
/// even when a column is genuinely <c>NULL</c>, and <c>ExecuteQuery</c> hands back <c>object</c> values whose
/// runtime type depends on the column, not on the declaration. Binding and reading therefore live here instead
/// of being re-derived at each call site.
/// <para>
/// No statement text is ever assembled from caller input: the table name in <see cref="Count"/> comes from a
/// closed enum, so the only values that reach the engine are bound parameters.
/// </para>
/// </remarks>
internal static class IndexSql
{
    /// <summary>
    /// Builds a parameter bag from name/value pairs.
    /// </summary>
    /// <param name="values">The pairs; names include their <c>@</c> prefix.</param>
    /// <returns>The bag the engine's overloads expect.</returns>
    public static Dictionary<string, object?> Parameters(params (string Name, object? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var parameters = new Dictionary<string, object?>(values.Length, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            parameters[name] = value;
        }

        return parameters;
    }

    /// <summary>
    /// Runs a statement that returns nothing.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <param name="sql">The statement, with <c>@name</c> placeholders for every value.</param>
    /// <param name="parameters">The bound values, or <see langword="null"/> for a statement without any.</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> or <paramref name="sql"/> is null.</exception>
    public static void Execute(IDatabase database, string sql, Dictionary<string, object?>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(sql);

        if (parameters is null)
        {
            database.ExecuteSQL(sql);
            return;
        }

        database.ExecuteSQL(sql, parameters);
    }

    /// <summary>
    /// Runs a query.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <param name="sql">The statement, with <c>@name</c> placeholders for every value.</param>
    /// <param name="parameters">The bound values, or <see langword="null"/> for a statement without any.</param>
    /// <returns>The rows; empty when nothing matched.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> or <paramref name="sql"/> is null.</exception>
    public static List<Dictionary<string, object?>> Query(
        IDatabase database,
        string sql,
        Dictionary<string, object?>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(sql);

        var rows = new List<Dictionary<string, object?>>();
        var result = parameters is null
            ? database.ExecuteQuery(sql)
            : database.ExecuteQuery(sql, parameters);

        foreach (var row in result)
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in row)
            {
                copy[key] = value;
            }

            rows.Add(copy);
        }

        return rows;
    }

    /// <summary>
    /// Counts the rows of one table.
    /// </summary>
    /// <param name="database">An open index database.</param>
    /// <param name="table">The table to count.</param>
    /// <returns>The row count, or zero when the table is empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    public static long Count(IDatabase database, IndexTable table)
    {
        ArgumentNullException.ThrowIfNull(database);

        // A switch, not interpolation: the statement text stays a constant that no caller value can reach.
        var sql = table switch
        {
            IndexTable.Files => "SELECT COUNT(*) FROM files",
            IndexTable.Chunks => "SELECT COUNT(*) FROM chunks",
            IndexTable.Symbols => "SELECT COUNT(*) FROM symbols",
            IndexTable.Edges => "SELECT COUNT(*) FROM edges",
            IndexTable.Docs => "SELECT COUNT(*) FROM docs",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unknown index table."),
        };

        var rows = Query(database, sql);
        return rows.Count == 0 || rows[0].Count == 0 ? 0 : AsLong(rows[0].Values.First());
    }

    /// <summary>
    /// Reads a value as a number.
    /// </summary>
    /// <param name="value">The raw column value, possibly <see langword="null"/> or <see cref="DBNull"/>.</param>
    /// <returns>The number, or zero for an absent value.</returns>
    public static long AsLong(object? value) =>
        Normalise(value) is { } present ? Convert.ToInt64(present, CultureInfo.InvariantCulture) : 0;

    /// <summary>
    /// Reads a value as text.
    /// </summary>
    /// <param name="value">The raw column value, possibly <see langword="null"/> or <see cref="DBNull"/>.</param>
    /// <returns>The text, or the empty string for an absent value.</returns>
    public static string AsText(object? value) => Normalise(value)?.ToString() ?? string.Empty;

    /// <summary>
    /// Reads a value as optional text.
    /// </summary>
    /// <param name="value">The raw column value, possibly <see langword="null"/> or <see cref="DBNull"/>.</param>
    /// <returns>The text, or <see langword="null"/> when the column is absent or empty.</returns>
    public static string? AsOptionalText(object? value) =>
        Normalise(value)?.ToString() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// Turns the engine's "no value" marker into the null this assembly works with.
    /// </summary>
    /// <param name="value">The raw column value.</param>
    /// <returns>The value, or <see langword="null"/> for <see cref="DBNull"/>.</returns>
    /// <remarks>
    /// SharpCoreDB returns <see cref="DBNull"/> for a column that was never written and <see langword="null"/>
    /// for one that was written as null, so both have to mean the same thing here — otherwise
    /// <see cref="Convert.ToInt64(object, IFormatProvider)"/> throws on a row that merely omitted a column with a
    /// default.
    /// </remarks>
    private static object? Normalise(object? value) => value is DBNull ? null : value;
}

/// <summary>
/// The tables a counter can be taken from.
/// </summary>
internal enum IndexTable
{
    /// <summary>The per-file rows.</summary>
    Files,

    /// <summary>The passages.</summary>
    Chunks,

    /// <summary>The declarations.</summary>
    Symbols,

    /// <summary>The typed relations between declarations.</summary>
    Edges,

    /// <summary>The documentation passages.</summary>
    Docs,
}
