using System.Text.Json;

namespace SharpCoreAgent.Evaluation;

/// <summary>Reads and validates the scenario format before a fixture is copied or a command is run.</summary>
internal static class EvaluationScenarioLoader
{
    public static async Task<IReadOnlyList<EvaluationScenario>> LoadAllAsync(
        string scenariosRoot,
        string? selectedId,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(scenariosRoot))
        {
            throw new EvaluationException($"scenario directory '{scenariosRoot}' does not exist");
        }

        var scenarios = new List<EvaluationScenario>();
        foreach (var directory in Directory.EnumerateDirectories(scenariosRoot).Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(directory, "scenario.json");
            if (File.Exists(path))
            {
                scenarios.Add(await LoadAsync(path, cancellationToken).ConfigureAwait(false));
            }
        }

        var duplicate = scenarios.GroupBy(scenario => scenario.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new EvaluationException($"duplicate scenario identifier '{duplicate.Key}'");
        }

        scenarios.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        if (selectedId is not null)
        {
            return scenarios.FirstOrDefault(scenario => scenario.Id == selectedId) is { } selected
                ? [selected]
                : throw new EvaluationException($"no scenario named '{selectedId}' exists");
        }

        return scenarios.Count > 0
            ? scenarios
            : throw new EvaluationException($"no scenario.json files were found under '{scenariosRoot}'");
    }

    public static async Task<EvaluationScenario> LoadAsync(string path, CancellationToken cancellationToken)
    {
        EvaluationScenario scenario;
        try
        {
            await using var stream = File.OpenRead(path);
            scenario = await JsonSerializer.DeserializeAsync(stream, EvaluationJsonContext.Default.EvaluationScenario, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new EvaluationException($"scenario '{path}' is empty");
        }
        catch (JsonException ex)
        {
            throw new EvaluationException($"scenario '{path}' is not valid: {ex.Message}");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        scenario = scenario with { Directory = directory };
        Validate(scenario, directory);
        return scenario;
    }

    private static void Validate(EvaluationScenario scenario, string directory)
    {
        if (scenario.SchemaVersion != 1)
        {
            throw new EvaluationException($"scenario '{scenario.Id}' uses unsupported schema version {scenario.SchemaVersion}");
        }

        if (!IsId(scenario.Id) || scenario.Id != Path.GetFileName(directory))
        {
            throw new EvaluationException($"scenario id '{scenario.Id}' must be a lowercase hyphenated name equal to its directory name");
        }

        if (string.IsNullOrWhiteSpace(scenario.Language) || string.IsNullOrWhiteSpace(scenario.Prompt))
        {
            throw new EvaluationException($"scenario '{scenario.Id}' needs a language and prompt");
        }

        if (!Directory.Exists(scenario.WorkspacePath))
        {
            throw new EvaluationException($"scenario '{scenario.Id}' has no workspace directory");
        }

        if (scenario.Turns.Count == 0 || scenario.Turns[^1].Calls.Count > 0 || string.IsNullOrWhiteSpace(scenario.Turns[^1].Text))
        {
            throw new EvaluationException($"scenario '{scenario.Id}' must end with a non-empty text-only response");
        }

        foreach (var call in scenario.Turns.SelectMany(turn => turn.Calls))
        {
            if (string.IsNullOrWhiteSpace(call.Name) || call.Arguments.ValueKind != JsonValueKind.Object)
            {
                throw new EvaluationException($"scenario '{scenario.Id}' has a call without a name or object arguments");
            }
        }

        foreach (var path in scenario.AllowedChanges)
        {
            ValidateRelativePath(scenario.Id, path);
        }

        if (scenario.AllowedChanges.Distinct(StringComparer.Ordinal).Count() != scenario.AllowedChanges.Count)
        {
            throw new EvaluationException($"scenario '{scenario.Id}' repeats an allowed change");
        }

        ValidateVerification(scenario);
        ValidateGrade(scenario);
    }

    private static void ValidateVerification(EvaluationScenario scenario)
    {
        var verification = scenario.Verification;
        if (verification.Tool is not ("dotnet_verify" or "run_shell"))
        {
            throw new EvaluationException($"scenario '{scenario.Id}' verifier must be dotnet_verify or run_shell");
        }

        if (verification.Tool == "run_shell" && string.IsNullOrWhiteSpace(verification.Command))
        {
            throw new EvaluationException($"scenario '{scenario.Id}' needs the exact run_shell verification command");
        }
    }

    private static void ValidateGrade(EvaluationScenario scenario)
    {
        var grade = scenario.Grade;
        if (string.IsNullOrWhiteSpace(grade.Command) || grade.TimeoutSeconds is < 1 or > 600)
        {
            throw new EvaluationException($"scenario '{scenario.Id}' needs a grade command and a timeout from 1 to 600 seconds");
        }

        if (grade.ExpectedOutput.Any(string.IsNullOrWhiteSpace))
        {
            throw new EvaluationException($"scenario '{scenario.Id}' has a blank expected-output fragment");
        }

        foreach (var file in grade.ExpectedFiles)
        {
            ValidateRelativePath(scenario.Id, file.Path);
            if (file.Contains.Count == 0 || file.Contains.Any(fragment => fragment is null))
            {
                throw new EvaluationException($"scenario '{scenario.Id}' expected file '{file.Path}' has no content assertions");
            }
        }
    }

    private static void ValidateRelativePath(string id, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new EvaluationException($"scenario '{id}' path '{path}' must be relative and non-empty");
        }

        var basePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".evaluation-validation"));
        var rooted = Path.GetFullPath(path, basePath);
        var relative = Path.GetRelativePath(basePath, rooted);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new EvaluationException($"scenario '{id}' path '{path}' escapes its workspace");
        }
    }

    private static bool IsId(string id) =>
        !string.IsNullOrWhiteSpace(id)
        && char.IsAsciiLetterOrDigit(id[0])
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
        && id.All(character => !char.IsAsciiLetter(character) || char.IsLower(character));
}

