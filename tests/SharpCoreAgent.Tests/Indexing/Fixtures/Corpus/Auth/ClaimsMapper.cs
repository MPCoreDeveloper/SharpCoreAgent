namespace Demo.Auth;

/// Turns a token's scope claims into the role names the application checks.
public static class ClaimsMapper
{
    private static readonly Dictionary<string, string> RolesByScope = new(StringComparer.Ordinal)
    {
        ["repo:read"] = "reader",
        ["repo:write"] = "writer",
        ["tenant:admin"] = "administrator",
    };

    public static IReadOnlyCollection<string> Roles(IEnumerable<string> scopes) =>
    [
        .. scopes
            .Where(RolesByScope.ContainsKey)
            .Select(scope => RolesByScope[scope])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];
}
