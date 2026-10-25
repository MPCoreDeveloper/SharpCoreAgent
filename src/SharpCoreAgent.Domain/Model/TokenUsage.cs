namespace SharpCoreAgent.Domain.Model;

/// <summary>
/// Token counts for one request or one turn.
/// </summary>
/// <remarks>
/// Both halves are nullable on purpose. Plenty of OpenAI-compatible endpoints omit usage on a stream,
/// and a zero would be a lie that silently under-reports spend. "Unknown" propagates: adding a known
/// usage to an unknown one yields unknown rather than a number that is too small.
/// </remarks>
public sealed record TokenUsage(int? InputTokens, int? OutputTokens)
{
    /// <summary>Gets the usage of a request whose counts were not reported.</summary>
    public static TokenUsage Unknown { get; } = new(null, null);

    /// <summary>Gets the total, or <see langword="null"/> when either half is unknown.</summary>
    public int? Total => InputTokens is null || OutputTokens is null ? null : InputTokens + OutputTokens;

    /// <summary>
    /// Adds two usages, keeping unknown as unknown.
    /// </summary>
    /// <param name="left">The running total.</param>
    /// <param name="right">The usage to add.</param>
    /// <returns>The sum, or <see cref="Unknown"/> in the affected half.</returns>
    public static TokenUsage operator +(TokenUsage left, TokenUsage right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new TokenUsage(Add(left.InputTokens, right.InputTokens), Add(left.OutputTokens, right.OutputTokens));
    }

    /// <summary>Gets a value indicating whether neither half is known.</summary>
    public bool IsUnknown => InputTokens is null && OutputTokens is null;

    /// <inheritdoc />
    public override string ToString() => Total is null
        ? $"{Format(InputTokens)} in / {Format(OutputTokens)} out (total unknown)"
        : $"{InputTokens} in / {OutputTokens} out";

    private static int? Add(int? left, int? right) => left is null || right is null ? null : left + right;

    private static string Format(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
}
