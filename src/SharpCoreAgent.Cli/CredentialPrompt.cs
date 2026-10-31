using System.Text;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Infrastructure.Configuration;
using SharpCoreAgent.Infrastructure.Credentials;
using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Asks for a key in the terminal and writes it where the next run will find it.
/// </summary>
/// <remarks>
/// The first run of a product whose key lives in an environment variable is a run that fails, followed by a
/// search for the variable's name, an editor and a shell that has to be restarted afterwards. This is the
/// other answer: one question, no echo, and a file the process reads.
/// <para>
/// Two rules shape it. A key is never echoed, logged or repeated — not in the prompt, not in the confirmation,
/// not in a message about a file that could not be written. And nothing is written unless the developer
/// asked for it: a pipe, a script or an <c>n</c> leaves the behaviour exactly as it was before this class
/// existed, and the three documented routes are printed instead.
/// </para>
/// </remarks>
internal sealed class CredentialPrompt
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly Func<bool> _isInteractive;
    private readonly Func<string?> _readSecret;
    private readonly Func<string, string?> _readEnvironment;
    private readonly Action<string, string?> _setEnvironment;
    private readonly string? _credentialPath;
    private readonly string? _settingsPath;

    /// <summary>
    /// Creates a prompt.
    /// </summary>
    /// <param name="input">Where the answers are read from, normally <see cref="Console.In"/>.</param>
    /// <param name="output">Where the questions and the confirmations go.</param>
    /// <param name="error">Where a refusal or a failed write is reported.</param>
    /// <param name="isInteractive">The terminal probe; a script is never asked.</param>
    /// <param name="credentialPath">The file to write, or <see langword="null"/> for the default one.</param>
    /// <param name="settingsPath">The settings file to record <c>credential-file</c> in, when the default path is used.</param>
    /// <param name="readSecret">The masked reader; defaults to the terminal's own, which echoes nothing.</param>
    /// <param name="readEnvironment">The environment reader, used to place the default file and to read a setting.</param>
    /// <param name="setEnvironment">Where a pasted key is put for this process; defaults to the environment.</param>
    public CredentialPrompt(
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<bool> isInteractive,
        string? credentialPath = null,
        string? settingsPath = null,
        Func<string?>? readSecret = null,
        Func<string, string?>? readEnvironment = null,
        Action<string, string?>? setEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(isInteractive);

        _input = input;
        _output = output;
        _error = error;
        _isInteractive = isInteractive;
        _credentialPath = credentialPath;
        _settingsPath = settingsPath;
        _readSecret = readSecret ?? (() => ReadMasked(output));
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
        _setEnvironment = setEnvironment ?? Environment.SetEnvironmentVariable;
    }

    /// <summary>
    /// Builds the prompt a command line asks for: the process's console, and the credential file the
    /// command line resolved.
    /// </summary>
    /// <param name="options">The parsed command line, for the credential file it names.</param>
    /// <param name="settingsPath">The settings file this process resolved.</param>
    /// <returns>The prompt the preflight uses.</returns>
    public static CredentialPrompt ForConsole(CliOptions options, string settingsPath)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new CredentialPrompt(
            Console.In,
            Console.Out,
            Console.Error,
            Terminal.Interactive,
            options.CredentialFile,
            settingsPath);
    }

    /// <summary>
    /// Asks for a key for each provider that has none, and reports whether every model is covered now.
    /// </summary>
    /// <param name="models">The models the process may send a request to.</param>
    /// <param name="credentials">The store the answers have to satisfy, read after each paste.</param>
    /// <returns><see langword="true"/> when every model's provider has a key.</returns>
    /// <remarks>
    /// One question per provider rather than per model: two roles naming the same backend are one key, and
    /// asking twice for it would read as a bug. A provider that needs no key (the demo adapter) is never
    /// mentioned.
    /// </remarks>
    public bool TryFix(IReadOnlyList<ModelDescriptor> models, ICredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(credentials);

        if (!_isInteractive())
        {
            return false;
        }

        foreach (var provider in Missing(models, credentials))
        {
            var names = ModelCatalog.CredentialNames(provider);
            var path = CredentialPath();

            _output.WriteLine($"{provider} needs a key, and none is set.");
            _output.WriteLine($"  set {string.Join(" or ", names)}, or paste one now: it is written to {path}");
            _output.Write($"  paste {names[0]}? [Y/n] ");
            _output.Flush();

            var answer = _input.ReadLine()?.Trim();
            if (answer is { Length: > 0 } && !IsYes(answer))
            {
                continue;
            }

            var secret = _readSecret()?.Trim();
            if (string.IsNullOrEmpty(secret))
            {
                _error.WriteLine("  nothing was pasted, so nothing was written.");
                continue;
            }

            Remember(names[0], secret, path);
        }

        return Missing(models, credentials).Count == 0;
    }

    /// <summary>
    /// Lists the providers that still have no key, in the order the models named them.
    /// </summary>
    /// <param name="models">The models the process may use.</param>
    /// <param name="credentials">The store to ask.</param>
    /// <returns>The provider keys, each once.</returns>
    private static List<string> Missing(IReadOnlyList<ModelDescriptor> models, ICredentialStore credentials) =>
    [
        .. models
            .Select(model => model.Provider)
            .Where(provider => !HasKey(provider, credentials))
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Answers whether a provider already has a key, without reporting anything.
    /// </summary>
    /// <param name="provider">The provider key.</param>
    /// <param name="credentials">The store to ask.</param>
    /// <returns><see langword="true"/> when a key is set, or the provider needs none.</returns>
    private static bool HasKey(string provider, ICredentialStore credentials)
    {
        var names = ModelCatalog.CredentialNames(provider);
        return names.Count == 0 || names.Any(name => credentials.Find(name) is not null);
    }

    /// <summary>
    /// Writes one key, makes it visible to this process, and remembers where it went.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <param name="secret">The key, which is never printed.</param>
    /// <param name="path">The file to write.</param>
    private void Remember(string name, string secret, string path)
    {
        try
        {
            CredentialFile.Upsert(path, name, secret);
        }
        catch (Exception ex) when (ex is CredentialFileException or ArgumentException)
        {
            _error.WriteLine($"error: could not write {name} to '{path}': {ex.Message}");
            return;
        }

        // The store reads this name at request time, so the next turn works without a restart. A child
        // process started by a tool or the verifier is given an environment with every *_API_KEY name
        // removed, so the key does not travel with a build or a test run.
        _setEnvironment(name, secret);
        _output.WriteLine($"  saved {name} to {path}");

        if (_credentialPath is null)
        {
            Adopt(path);
        }
    }

    /// <summary>
    /// Records the file in the settings document, so the next run reads it without being told.
    /// </summary>
    /// <param name="path">The credential file that was just written.</param>
    /// <remarks>
    /// Only when the developer named no file: an explicit <c>--credential-file</c> or
    /// <c>SHARPCOREAGENT_CREDENTIAL_FILE</c> is a decision about the invocation, and a paste must not turn it
    /// into a preference. A settings file that cannot be written is a warning rather than a failure — the key
    /// is already in place for this process.
    /// </remarks>
    private void Adopt(string path)
    {
        if (_settingsPath is null)
        {
            return;
        }

        try
        {
            var document = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (existingName, existingValue) in AgentSettingsFile.Read(_settingsPath))
            {
                document[existingName] = existingValue;
            }

            document["credential-file"] = path;
            AgentSettingsFile.Save(_settingsPath, document);
            _output.WriteLine($"  next run reads it too: credential-file is now in {_settingsPath}");
        }
        catch (ConfigurationFileException ex)
        {
            _error.WriteLine($"warning: {ex.Message} — pass --credential-file '{path}' next time.");
        }
    }

    /// <summary>
    /// Names the file a key would be written to, without writing anything.
    /// </summary>
    /// <returns>The credential file, absolute.</returns>
    private string CredentialPath() => _credentialPath ?? CredentialFile.DefaultPath(_readEnvironment);

    /// <summary>
    /// Reads a key with the terminal's echo off.
    /// </summary>
    /// <param name="output">Where the newline that ends the line goes, so the next line starts clean.</param>
    /// <returns>The key as typed, or an empty string when the developer pressed Escape.</returns>
    /// <remarks>
    /// A redirected stdin has no echo to suppress and <see cref="Console.ReadKey(bool)"/> refuses to run
    /// there, so that case falls back to a plain read: the caller has already asked whether anyone is
    /// there, and a script that pipes a wrapped key gets the line it wrote.
    /// </remarks>
    private static string? ReadMasked(TextWriter output)
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                output.WriteLine();
                return text.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                output.WriteLine();
                return string.Empty;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }

    /// <summary>Reads an answer as yes: <c>y</c> or <c>yes</c>, in any case. An empty line is yes too.</summary>
    /// <param name="answer">The line the developer typed.</param>
    /// <returns><see langword="true"/> when the answer is a yes.</returns>
    private static bool IsYes(string answer) =>
        answer.Equals("y", StringComparison.OrdinalIgnoreCase)
        || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
