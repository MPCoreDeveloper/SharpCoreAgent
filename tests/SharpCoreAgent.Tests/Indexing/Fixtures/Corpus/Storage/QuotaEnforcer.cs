namespace Demo.Storage;

/// Refuses a write that would push a tenant past its byte quota.
public sealed class QuotaEnforcer(IQuotaLedger ledger)
{
    public void Reserve(string tenant, long bytes)
    {
        var limit = ledger.LimitBytes(tenant);
        var used = ledger.UsedBytes(tenant) + bytes;
        if (used > limit)
        {
            throw new InvalidOperationException($"tenant {tenant} is over quota: {used} of {limit} bytes");
        }

        ledger.Add(tenant, bytes);
    }
}

public interface IQuotaLedger
{
    long UsedBytes(string tenant);

    long LimitBytes(string tenant);

    void Add(string tenant, long bytes);
}
