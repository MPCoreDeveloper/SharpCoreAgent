using System.Globalization;
using System.Text.Json;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Infrastructure.Credentials;
using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.Evaluation;

/// <summary>The maintainer command: discover, run, persist and optionally compare an evaluation report.</summary>
internal static class EvaluationCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            var options = EvaluationArguments.Parse(args);
            if (options.Help)
            {
                await output.WriteLineAsync(EvaluationArguments.HelpText.AsMemory(), cancellationToken).ConfigureAwait(false);
                return 0;
            }

            var repository = FindRepository(options.Repository);
            var execution = ExecutionFor(options);
            var destination = Path.GetFullPath(
                options.Output
                ?? Path.Combine(repository, "artifacts", "evals", execution.Live ? "live.json" : "latest.json"));
            var recordedBaselinePath = Path.GetFullPath(Path.Combine(repository, "eng", "evals", "baseline.json"));
            var baselinePath = options.Check
                ? Path.GetFullPath(options.Baseline ?? recordedBaselinePath)
                : null;
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(destination, baselinePath, pathComparison)
                || (execution.Live && string.Equals(destination, recordedBaselinePath, pathComparison)))
            {
                throw new EvaluationException("the evaluation cannot write to the recorded baseline; choose a different --output path");
            }

            EnsureLiveCredentials(execution);
            var baseline = baselinePath is null ? null : await ReadReportAsync(baselinePath, cancellationToken).ConfigureAwait(false);
            var scenarios = await EvaluationScenarioLoader.LoadAllAsync(
                Path.Combine(repository, "eng", "evals", "scenarios"), options.Scenario, cancellationToken)
                .ConfigureAwait(false);
            var report = await EvaluationRunner.RunAllAsync(scenarios, execution, cancellationToken).ConfigureAwait(false);
            await PrintAsync(report, output, cancellationToken).ConfigureAwait(false);
            await WriteReportAsync(destination, report, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"report: {destination}".AsMemory(), cancellationToken).ConfigureAwait(false);

            var regressions = Array.Empty<string>();
            if (baseline is not null)
            {
                regressions = [.. BaselineComparer.Regressions(baseline, report)];
                foreach (var regression in regressions)
                {
                    await error.WriteLineAsync($"regression: {regression}".AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                if (regressions.Length == 0)
                {
                    await output.WriteLineAsync("baseline: no semantic regressions".AsMemory(), cancellationToken).ConfigureAwait(false);
                }
            }

            return report.Summary.Attempted > 0 && report.Summary.Failed == 0 && regressions.Length == 0 ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("evaluation cancelled".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return 4;
        }
        catch (EvaluationException ex)
        {
            await error.WriteLineAsync($"error: {ex.Message}".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return 2;
        }
        catch (Exception ex) when (ex is CredentialFileException or ArgumentException)
        {
            await error.WriteLineAsync($"error: {ex.Message}".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await error.WriteLineAsync($"evaluation failed: {ex.Message}".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
    }

    internal static string FindRepository(string? named)
    {
        if (named is not null)
        {
            var root = Path.GetFullPath(named);
            return File.Exists(Path.Combine(root, "SharpCoreAgent.slnx"))
                ? root
                : throw new EvaluationException($"'{root}' is not a SharpCoreAgent checkout");
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SharpCoreAgent.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new EvaluationException("could not find SharpCoreAgent.slnx; pass --repository <path>");
    }

    private static async Task PrintAsync(EvaluationReport report, TextWriter output, CancellationToken cancellationToken)
    {
        foreach (var scenario in report.Scenarios)
        {
            await output.WriteLineAsync(
                $"{scenario.Status.ToUpperInvariant()} {scenario.Id} · {scenario.Iterations} iteration(s) · {scenario.ToolCalls} tool call(s)".AsMemory(),
                cancellationToken).ConfigureAwait(false);
            foreach (var failure in scenario.Failures)
            {
                await output.WriteLineAsync($"  {failure}".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        var summary = report.Summary;
        var rate = summary.SuccessRate is { } value ? value.ToString("P1", CultureInfo.InvariantCulture) : "n/a";
        await output.WriteLineAsync(
            $"summary: {summary.Passed} passed · {summary.Failed} failed · {summary.Skipped} skipped · success {rate}".AsMemory(),
            cancellationToken).ConfigureAwait(false);
        var input = summary.InputTokens?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown";
        var tokens = summary.OutputTokens?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown";
        await output.WriteLineAsync(
            $"metrics: {report.Mode} {input} in / {tokens} out · {summary.UnintendedChanges} unexpected change(s)".AsMemory(),
            cancellationToken).ConfigureAwait(false);
        if (report.Mode == "live")
        {
            await output.WriteLineAsync(
                $"provider: {report.Provider ?? "unknown"} · model: {report.Model ?? "provider default"}".AsMemory(),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static EvaluationExecution ExecutionFor(EvaluationArguments options) => new()
    {
        Live = options.Live,
        Provider = options.Provider ?? ModelCatalog.DefaultProvider,
        Model = options.Model,
        BaseUrl = options.BaseUrl,
        CredentialFile = options.CredentialFile,
        MaxIterations = options.MaxIterations ?? 40,
        MaxOutputTokens = options.MaxOutputTokens ?? 4_000,
        MaxTotalTokens = options.MaxTotalTokens ?? 250_000,
    };

    private static void EnsureLiveCredentials(EvaluationExecution execution)
    {
        if (!execution.Live)
        {
            return;
        }

        var descriptor = ModelCatalog.DescriptorFor(execution.Provider, execution.Model);
        var names = ModelCatalog.CredentialNames(descriptor.Provider);
        if (names.Count == 0)
        {
            return;
        }

        var store = EnvironmentCredentialStore.FromFile(execution.CredentialFile);
        if (!names.Any(store.IsConfigured))
        {
            throw new EvaluationException(
                $"live evaluation needs one of {string.Join(", ", names)} for provider '{descriptor.Provider}'");
        }
    }

    private static async Task<EvaluationReport> ReadReportAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new EvaluationException($"baseline '{path}' does not exist");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var report = await JsonSerializer.DeserializeAsync(stream, EvaluationJsonContext.Default.EvaluationReport, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new EvaluationException($"baseline '{path}' is empty");
            if (report.SchemaVersion != 1 || report.Mode != "recorded")
            {
                throw new EvaluationException($"baseline '{path}' is not a version-1 recorded report");
            }

            if (report.Scenarios.Select(scenario => scenario.Id).Distinct(StringComparer.Ordinal).Count() != report.Scenarios.Count)
            {
                throw new EvaluationException($"baseline '{path}' contains duplicate scenario identifiers");
            }

            return report;
        }
        catch (JsonException ex)
        {
            throw new EvaluationException($"baseline '{path}' is not valid: {ex.Message}");
        }
    }

    private static async Task WriteReportAsync(string path, EvaluationReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(report, EvaluationJsonContext.Default.EvaluationReport) + "\n";
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
