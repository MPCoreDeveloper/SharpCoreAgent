using SharpCoreAgent.Infrastructure.Credentials;
using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.Cli;

/// <summary>
/// <c>sharpcoreagent credentials --check</c>: which keys each provider needs, whether they are configured, and where
/// each one was found — never the value itself.
/// </summary>
/// <remarks>
/// This is the diagnostic for the failure mode where a key is set in one terminal and not the one running the agent.
/// The answer is deliberately provenance, not a secret: a file is named with its line, the environment is named as
/// <c>environment</c>, and a missing key stays missing.
/// </remarks>
internal static class CredentialsCommand
{
    /// <summary>Runs the check.</summary>
    /// <param name="cli">The parsed command line; <c>--provider</c> narrows the report.</param>
    /// <param name="readEnvironment">The environment reader, injected for tests.</param>
    /// <param name="output">Where the report goes.</param>
    /// <returns>0 when every listed provider has a key, 1 when one does not.</returns>
    public static int Execute(CliOptions cli, Func<string, string?> readEnvironment, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        ArgumentNullException.ThrowIfNull(output);

        var filePath = cli.CredentialFile is { Length: > 0 } path ? Path.GetFullPath(path) : null;
        var entries = filePath is null
            ? new Dictionary<string, CredentialEntry>(StringComparer.Ordinal)
            : CredentialFile.ReadEntries(filePath).ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var providers = cli.ProviderExplicit ? [cli.Provider] : ModelCatalog.Providers;

        output.WriteLine("provider    credential                  source");
        var missing = false;
        foreach (var provider in providers)
        {
            var names = ModelCatalog.CredentialNames(provider);
            if (names.Count == 0)
            {
                output.WriteLine($"{provider,-12}(no credential required)");
                continue;
            }

            var configured = false;
            foreach (var name in names)
            {
                string source;
                if (entries.TryGetValue(name, out var entry))
                {
                    source = $"file:{filePath}:{entry.Line}";
                    configured = true;
                }
                else if (!string.IsNullOrWhiteSpace(readEnvironment(name)))
                {
                    source = "environment";
                    configured = true;
                }
                else
                {
                    source = "missing";
                }

                output.WriteLine($"{provider,-12}{name,-28}{source}");
            }

            if (!configured)
            {
                missing = true;
            }
        }

        return missing ? 1 : 0;
    }
}
