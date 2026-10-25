using SharpCoreAgent.Infrastructure.Credentials;
using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.Cli;

/// <summary>
/// <c>sharpcoreagent models</c>: what this build can talk to, and what is missing before it can.
/// </summary>
/// <remarks>
/// The command reads the catalogue rather than calling a provider, so it works offline and answers the
/// question a developer actually has when a turn refuses to start: which models exist, and is the key
/// for this one configured. A provider that needs no credential says nothing extra.
/// </remarks>
internal static class ModelsCommand
{
    /// <summary>
    /// Prints the catalogue.
    /// </summary>
    /// <param name="cli">The parsed command line; <c>--provider</c> narrows the list to one provider.</param>
    /// <returns>The process exit code, always <see cref="ExitCodes.Success"/>.</returns>
    public static int Execute(CliOptions cli)
    {
        ArgumentNullException.ThrowIfNull(cli);

        var credentials = new EnvironmentCredentialStore();
        IReadOnlyList<string> providers = cli.ProviderExplicit ? [cli.Provider] : ModelCatalog.Providers;

        foreach (var provider in providers)
        {
            foreach (var model in ModelCatalog.ForProvider(provider))
            {
                Console.Out.WriteLine(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{model.QualifiedName}  {model.DisplayName}  tools={(model.SupportsTools ? "yes" : "no")}"));
            }

            var names = ModelCatalog.CredentialNames(provider);
            if (names.Count > 0 && !names.Any(credentials.IsConfigured))
            {
                Console.Out.WriteLine($"note: set {string.Join(" or ", names)} to use {provider}");
            }
        }

        return ExitCodes.Success;
    }
}
