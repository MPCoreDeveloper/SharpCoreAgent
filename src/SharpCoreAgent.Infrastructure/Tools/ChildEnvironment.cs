using System.Diagnostics;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// The environment a child process is given, in one place.
/// </summary>
/// <remarks>
/// Two tools now start a process — the shell and the compiler — and both of them must run without the
/// credentials that pay for the conversation (Principle VIII). One implementation rather than two is what stops
/// a name filter from being fixed in the shell and forgotten in the verifier.
/// </remarks>
internal static class ChildEnvironment
{
    /// <summary>
    /// Removes every provider credential from the child's environment.
    /// </summary>
    /// <param name="startInfo">The start information to filter, in place.</param>
    /// <exception cref="ArgumentNullException"><paramref name="startInfo"/> is null.</exception>
    public static void RemoveSecrets(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        foreach (var name in startInfo.Environment.Keys.Where(IsSecretName).ToArray())
        {
            startInfo.Environment.Remove(name);
        }
    }

    private static bool IsSecretName(string name) =>
        name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_PASSWORD", StringComparison.OrdinalIgnoreCase);
}
