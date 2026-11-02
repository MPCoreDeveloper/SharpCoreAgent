using System.Net;
using SharpCoreAgent.Cli;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The page as a pure function of the status document: what it shows, what it refuses to invent, and what it needs.
/// </summary>
/// <remarks>
/// No socket and no index: the page takes the dictionary <see cref="IndexStatusDocument.Build"/> produces, so a field the
/// document does not carry cannot appear on the page, and this file can assert that without a workspace that has one.
/// </remarks>
public sealed class UiPageTests
{
    /// <summary>Every field FR-009 names, which is every field a developer reads.</summary>
    private static readonly string[] ReadableFields =
    [
        "state",
        "tier",
        "embedder",
        "builtUtc",
        "files",
        "stale",
        "skipped",
        "orphans",
        "passages",
        "symbols",
        "edges",
        "communities",
        "sizeBytes",
    ];

    [Fact]
    public void EveryReadableFieldAppearsWithItsValue()
    {
        var html = Render(Fresh());

        foreach (var field in ReadableFields)
        {
            Assert.Contains($"data-field=\"{field}\"", html, StringComparison.Ordinal);
        }

        Assert.Contains(">1 908<", html, StringComparison.Ordinal);
        Assert.Contains(">14.2 MiB<", html, StringComparison.Ordinal);
        Assert.Contains(">hashed-local/512<", html, StringComparison.Ordinal);
        Assert.Contains(">2026-11-02T09:30:00Z<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingIndexRendersTheHintAndNoCardOfZeroes()
    {
        var html = Render(IndexStatus.Missing("C:/demo"));

        Assert.Contains("data-field=\"state\"", html, StringComparison.Ordinal);
        Assert.Contains(">missing<", html, StringComparison.Ordinal);
        Assert.Contains("run <code>sharpcoreagent index</code> to build it", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"card\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-field=\"files\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkspaceIsEscapedAndKeepsItsCharacters()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var document = IndexStatusDocument.Build(WorkspaceFor(sandbox), Fresh());
        document["workspace"] = "C:/Répertoire & <scripts>";

        var html = UiPage.Render(document);

        // The markup is escaped — `&` and `<` are entities, so a path cannot become a tag — and the text a reader sees is
        // still the path: HtmlEncode also writes anything above 159 as a numeric entity, which is why the decoded page is
        // what the round trip is asserted on.
        Assert.Contains("&amp; &lt;scripts&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<scripts>", html, StringComparison.Ordinal);
        Assert.Contains("C:/Répertoire & <scripts>", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSizeIsTheFormattedStringRatherThanTheRawCount()
    {
        var html = Render(Fresh());

        Assert.Contains(">14.2 MiB<", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">14900224<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void APartialBuildSaysSoBesideTheLastCompleteNumbers()
    {
        var html = Render(Fresh() with { Freshness = IndexFreshness.Partial });

        Assert.Contains(">partial<", html, StringComparison.Ordinal);
        Assert.Contains("a build is in progress", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BothColourSchemesAreDefinedWithTheirOwnPalette()
    {
        var html = Render(Fresh());

        Assert.Contains("prefers-color-scheme: dark", html, StringComparison.Ordinal);
        Assert.Contains("color-scheme: light dark", html, StringComparison.Ordinal);
        Assert.Contains("--bg: #ffffff", html, StringComparison.Ordinal);
        Assert.Contains("--bg: #16161a", html, StringComparison.Ordinal);
        Assert.Contains("--fg: #e8e8ee", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingOnThePageComesFromAnotherOrigin()
    {
        var html = Render(Fresh());

        // No URL of any kind: the stylesheet is inline, the icon is a data: URI with nothing in it, and the one script is
        // in the document. A panel that reached a CDN would be a request somebody else's server logs.
        Assert.DoesNotContain("://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<link rel=\"stylesheet\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@import", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOneScriptReadsTheOneRoute()
    {
        var html = Render(Fresh());

        Assert.Equal(1, html.Split("<script").Length - 1);
        Assert.Contains("/api/status.json", html, StringComparison.Ordinal);
        Assert.DoesNotContain("location.origin +", html, StringComparison.Ordinal);
    }

    private static IndexStatus Fresh() => new()
    {
        WorkspaceRoot = "C:/demo",
        Freshness = IndexFreshness.Fresh,
        Tier = IndexTier.Lean,
        Embedder = "hashed-local/512",
        Dimensions = 512,
        Files = 1908,
        Chunks = 4200,
        Symbols = 812,
        Edges = 1204,
        Communities = 41,
        StaleFiles = 0,
        SkippedFiles = 3,
        OrphanRows = 0,
        VectorSearchEnabled = true,
        BuiltUtc = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero),
        SizeBytes = 14900224,
    };

    private static string Render(IndexStatus status)
    {
        using var sandbox = ToolTestWorkspace.Create();
        return UiPage.Render(IndexStatusDocument.Build(WorkspaceFor(sandbox), status));
    }

    private static Workspace WorkspaceFor(ToolTestWorkspace sandbox) =>
        CliOptions.Parse(["--workspace", sandbox.Root], _ => null).ToSharpCoreAgentOptions().CreateWorkspace();
}
