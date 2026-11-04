namespace SharpCoreAgent.Evaluation;

/// <summary>Closed command-line surface for the evaluation executable.</summary>
internal sealed record EvaluationArguments
{
    public const string HelpText = """
        SharpCoreAgent recorded coding-task evaluation

        usage: dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- [options]

          --repository <path>  checkout root; otherwise discovered from the current directory
          --scenario <id>      run one scenario; cannot be combined with --check
          --output <path>      JSON destination; default artifacts/evals/latest.json
          --check              compare against the checked-in baseline without changing it
          --baseline <path>    comparison document; only valid with --check
          --help               print this text

        exit codes: 0 pass, 1 task failure/regression, 2 bad input, 4 cancelled.
        """;

    public string? Repository { get; private init; }
    public string? Scenario { get; private init; }
    public string? Output { get; private init; }
    public string? Baseline { get; private init; }
    public bool Check { get; private init; }
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
                case "--check": options = options with { Check = true }; break;
                case "--help" or "-h": options = options with { Help = true }; break;
                default: throw new EvaluationException($"unknown argument '{args[index]}'; use --help");
            }
        }

        if (options.Check && options.Scenario is not null)
        {
            throw new EvaluationException("--scenario cannot be combined with --check because a partial run is not a baseline");
        }

        if (!options.Check && options.Baseline is not null)
        {
            throw new EvaluationException("--baseline is meaningful only with --check");
        }

        return options;
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
