namespace Demo.Scheduling;

/// Runs one scheduled job, records how long it took, and reports a failure instead of throwing.
public sealed class JobRunner(ILogger log)
{
    public async Task<JobOutcome> RunAsync(IScheduledJob job, CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        try
        {
            await job.ExecuteAsync(cancellationToken);
            return new JobOutcome(job.Name, DateTimeOffset.UtcNow - startedUtc, fault: null);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            log.Error(failure, "scheduled job {Job} failed", job.Name);
            return new JobOutcome(job.Name, DateTimeOffset.UtcNow - startedUtc, failure);
        }
    }
}
