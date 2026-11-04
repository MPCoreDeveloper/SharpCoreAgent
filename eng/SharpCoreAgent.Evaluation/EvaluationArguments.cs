namespace SharpCoreAgent.Evaluation;

/// <summary>Closed command-line surface for the evaluation executable.</summary>
internal sealed record EvaluationArguments
{
    public const string HelpText = """
        SharpCoreAgent coding-task evaluation

        usage: dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- [options]

          --repository <path>  checkout root; otherwise discovered from the current directory
          --scenario <id>      run one scenario; cannot be combined with --check
          --output <path>      JSON destination; default artifacts/evals/latest.json (recorded) or live.json
          --check              compare a recorded run against the checked-in baseline without changing it
          --baseline <path>    comparison document; only valid with --check
          --live               run the scenarios through the selected live provider; no --check
          --provider <name>    live provider; default deepseek
          --model <id>         live model id; default the provider's model
          --base-url <url>     endpoint required by the openai-compatible provider
          --credential-file <path>  NAME=value credential file for a live run
          --max-iterations <n> live turn ceiling per scenario; default 40
          --max-output-tokens <n> live response ceiling; default 4000
          --max-total-tokens <n> live session ceiling per scenario; default 250000
          --help               print this text

        exit codes: 0 pass, 1 task failure/regression, 2 bad input, 4 cancelled.
        """;

    public string? Repository { get; private init; }
    public string? Scenario { get; private init; }
    public string? Output { get; private init; }
    public string? Baseline { get; private init; }
    public string? Provider { get; private init; }
    public string? Model { get; private init; }
    public string? BaseUrl { get; private init; }
    public string? CredentialFile { get; private init; }
    public int? MaxIterations { get; private init; }
    public int? MaxOutputTokens { get; private init; }
    public long? MaxTotalTokens { get; private init; }
    public bool Check { get; private init; }
    public bool Live { get; private init; }
    public bool Help { get; private init; }

    public static EvaluationArguments Parse(string[] args)
    {
        var options = new EvaluationArguments();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--repository": options = options with { Repository = Value(args, ref index) }; break;
                case "--scenario": options = options with { Scenario = Value(args, ref index) }; break;
                case "--output": options = options with { Output = Value(args, ref index) }; break;
                case "--baseline": options = options with { Baseline = Value(args, ref index) }; break;
                case "--live": options = options with { Live = true }; break;
                case "--provider": options = options with { Provider = Value(args, ref index) }; break;
                case "--model": options = options with { Model = Value(args, ref index) }; break;
                case "--base-url": options = options with { BaseUrl = Value(args, ref index) }; break;
                case "--credential-file": options = options with { CredentialFile = Value(args, ref index) }; break;
                case "--max-iterations":
                    options = options with { MaxIterations = IntValue(args, ref index, 1, 1_000) };
                    break;
                case "--max-output-tokens":
                    options = options with { MaxOutputTokens = IntValue(args, ref index, 256, 64_000) };
                    break;
                case "--max-total-tokens":
                    options = options with { MaxTotalTokens = LongValue(args, ref index, 1_000, 100_000_000) };
                    break;
                case "--check": options = options with { Check = true }; break;
                case "--help" or "-h": options = options with { Help = true }; break;
                default: throw new EvaluationException($"unknown argument '{args[index]}'; use --help");
            }
        }

        if (options.Check && options.Live)
        {
            throw new EvaluationException("--check is only for the deterministic recorded mode; live results are not a baseline");
        }

        if (options.Check && options.Scenario is not null)
        {
            throw new EvaluationException("--scenario cannot be combined with --check because a partial run is not a baseline");
        }

        if (!options.Check && options.Baseline is not null)
        {
            throw new EvaluationException("--baseline is meaningful only with --check");
        }

        if (!options.Live
            && (options.Provider is not null
                || options.Model is not null
                || options.BaseUrl is not null
                || options.CredentialFile is not null
                || options.MaxIterations is not null
                || options.MaxOutputTokens is not null
                || options.MaxTotalTokens is not null))
        {
            throw new EvaluationException("--provider, --model, --base-url, --credential-file and the live limits require --live");
        }

        if (options.Live
            && string.Equals(options.Provider, "openai-compatible", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new EvaluationException("--base-url is required for the openai-compatible provider");
        }

        return options;
    }

    private static int IntValue(string[] args, ref int index, int minimum, int maximum)
    {
        var text = Value(args, ref index);
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
        {
            throw new EvaluationException($"{args[index - 1]} must be between {minimum} and {maximum}");
        }

        return value;
    }

    private static long LongValue(string[] args, ref int index, long minimum, long maximum)
    {
        var text = Value(args, ref index);
        if (!long.TryParse(text, out var value) || value < minimum || value > maximum)
        {
            throw new EvaluationException($"{args[index - 1]} must be between {minimum} and {maximum}");
        }

        return value;
    }

    private static string Value(string[] args, ref int index)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new EvaluationException($"{args[index - 1]} needs a value");
        }

        return args[index];
    }
}
