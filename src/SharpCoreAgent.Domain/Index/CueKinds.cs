namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// The closed vocabulary of framework cues <c>find_cues</c> can report.
/// </summary>
/// <remarks>
/// One list serves the tool's schema, its validation and its error message, so the three cannot drift apart. The
/// values are stable identifiers, not display text; a caller compares them ordinally.
/// </remarks>
public static class CueKinds
{
    /// <summary>Matches every cue kind.</summary>
    public const string Any = "any";

    /// <summary>A route or endpoint registration, for example <c>app.get('/users')</c>.</summary>
    public const string Route = "route";

    /// <summary>A test registration or test method, for example <c>def test_total</c> or <c>describe(</c>.</summary>
    public const string Test = "test";

    /// <summary>A dependency-injection registration or request, for example <c>Depends(</c> or <c>@Injectable</c>.</summary>
    public const string DependencyInjection = "dependency_injection";

    /// <summary>A database call, for example <c>db.query(</c> or an <c>INSERT INTO</c> statement.</summary>
    public const string DatabaseCall = "database_call";

    private static readonly string[] Names =
    [
        Any,
        Route,
        Test,
        DependencyInjection,
        DatabaseCall,
    ];

    /// <summary>Gets the closed list of values a cue kind may take, in schema order.</summary>
    public static IReadOnlyList<string> Supported { get; } = Array.AsReadOnly(Names);
}
