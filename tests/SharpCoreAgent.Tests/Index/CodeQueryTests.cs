using SharpCoreAgent.Domain.Index;
using Xunit;

namespace SharpCoreAgent.Tests.Index;

/// <summary>
/// <see cref="CodeQuery.Create"/> is the one gate every retrieval request passes through, for the tool and
/// for the CLI alike, so these tests pin the defaults, both bounds, the closed kind list and the path
/// normalisation that the index's own storage format depends on.
/// </summary>
/// <remarks>
/// No workspace, no database and no file system: the factory is pure, so every case here is a string in and
/// a record out.
/// </remarks>
public sealed class CodeQueryTests
{
    [Fact]
    public void DefaultsMatchTheSchema()
    {
        var query = CodeQuery.Create("turn loop");

        Assert.Equal("turn loop", query.Text);
        Assert.Null(query.Path);
        Assert.Equal(CodeQuery.AnyKind, query.Kind);
        Assert.Equal(8, query.K);
        Assert.Equal(1, query.Hops);
    }

    [Fact]
    public void SupportedKindsAreTheSchemaEnum()
    {
        string[] expected =
        [
            "any", "class", "interface", "record", "struct", "enum",
            "method", "property", "field", "doc", "config",
        ];

        Assert.Equal(expected, CodeQuery.SupportedKinds);
    }

    [Fact]
    public void TextIsTrimmed()
    {
        Assert.Equal("where is the loop", CodeQuery.Create("   where is the loop\t").Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void BlankTextIsRejected(string text)
    {
        Assert.ThrowsAny<ArgumentException>(() => CodeQuery.Create(text));
    }

    [Fact]
    public void NullTextIsRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => CodeQuery.Create(null!));
    }

    [Theory]
    [InlineData(-3, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(12, 12)]
    [InlineData(13, 12)]
    [InlineData(500, 12)]
    public void KIsClampedToTheBudget(int asked, int expected)
    {
        Assert.Equal(expected, CodeQuery.Create("x", k: asked).K);
    }

    [Theory]
    [InlineData(-2, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(99, 3)]
    public void HopsIsClampedToTheGraphDepth(int asked, int expected)
    {
        Assert.Equal(expected, CodeQuery.Create("x", hops: asked).Hops);
    }

    [Theory]
    [InlineData("class", "class")]
    [InlineData("  Method  ", "method")]
    [InlineData("INTERFACE", "interface")]
    [InlineData("Any", "any")]
    [InlineData("doc", "doc")]
    [InlineData("config", "config")]
    public void KindIsCanonicalised(string asked, string expected)
    {
        Assert.Equal(expected, CodeQuery.Create("x", kind: asked).Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingKindMeansAny(string? asked)
    {
        Assert.Equal(CodeQuery.AnyKind, CodeQuery.Create("x", kind: asked).Kind);
    }

    [Theory]
    [InlineData("module")]
    [InlineData("interface2")]
    [InlineData("classes")]
    public void UnknownKindIsRejected(string kind)
    {
        var exception = Assert.Throws<ArgumentException>(() => CodeQuery.Create("x", kind: kind));

        Assert.Contains("kind must be one of", exception.Message, StringComparison.Ordinal);
        Assert.Contains(kind, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src\\AgentLoop.cs", "src/AgentLoop.cs")]
    [InlineData("./src/AgentLoop.cs", "src/AgentLoop.cs")]
    [InlineData("src/", "src")]
    [InlineData("src\\domain\\", "src/domain")]
    [InlineData("  src/AgentLoop.cs  ", "src/AgentLoop.cs")]
    public void PathIsNormalisedToTheStoredForm(string asked, string expected)
    {
        Assert.Equal(expected, CodeQuery.Create("x", path: asked).Path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("./")]
    public void PathThatNamesTheWorkspaceBecomesNull(string? asked)
    {
        Assert.Null(CodeQuery.Create("x", path: asked).Path);
    }

    [Fact]
    public void RootedPathIsRejected()
    {
        var rooted = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgentLoop.cs");

        var exception = Assert.Throws<ArgumentException>(() => CodeQuery.Create("x", path: rooted));

        Assert.Contains("must be relative to the workspace root", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("src/../../secrets.txt")]
    [InlineData("src\\..\\secrets.txt")]
    public void ClimbingPathIsRejected(string path)
    {
        var exception = Assert.Throws<ArgumentException>(() => CodeQuery.Create("x", path: path));

        Assert.Contains("must stay inside the workspace root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PathWithNulIsRejected()
    {
        Assert.Throws<ArgumentException>(() => CodeQuery.Create("x", path: "src/a\0b.cs"));
    }

    [Fact]
    public void SupportedEvidenceKindsAreTheSchemaEnum()
    {
        string[] expected = ["any", "code", "document", "configuration", "other"];

        Assert.Equal(expected, CodeQuery.SupportedEvidenceKinds);
    }

    [Fact]
    public void NewFiltersDefaultToNoRestriction()
    {
        var query = CodeQuery.Create("turn loop");

        Assert.Null(query.Language);
        Assert.Null(query.ExcludePathPrefix);
        Assert.Equal(CodeQuery.AnyEvidenceKind, query.EvidenceKind);
    }

    [Theory]
    [InlineData("CSharp", "csharp")]
    [InlineData("  Markdown  ", "markdown")]
    [InlineData("JSON", "json")]
    public void LanguageIsLowercasedAndTrimmed(string asked, string expected)
    {
        Assert.Equal(expected, CodeQuery.Create("x", language: asked).Language);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLanguageMeansAny(string? asked)
    {
        Assert.Null(CodeQuery.Create("x", language: asked).Language);
    }

    [Theory]
    [InlineData("docs\\generated", "docs/generated")]
    [InlineData("./docs/", "docs")]
    [InlineData("  tests/  ", "tests")]
    public void ExcludePathPrefixIsNormalisedToTheStoredForm(string asked, string expected)
    {
        Assert.Equal(expected, CodeQuery.Create("x", excludePathPrefix: asked).ExcludePathPrefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("./")]
    public void ExcludePathPrefixThatNamesTheWorkspaceBecomesNull(string? asked)
    {
        Assert.Null(CodeQuery.Create("x", excludePathPrefix: asked).ExcludePathPrefix);
    }

    [Fact]
    public void RootedExcludePathPrefixIsRejected()
    {
        var rooted = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "generated");

        Assert.Throws<ArgumentException>(() => CodeQuery.Create("x", excludePathPrefix: rooted));
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("src/../../secrets")]
    public void ClimbingExcludePathPrefixIsRejected(string prefix)
    {
        Assert.Throws<ArgumentException>(() => CodeQuery.Create("x", excludePathPrefix: prefix));
    }

    [Theory]
    [InlineData("CODE", "code")]
    [InlineData("  Document  ", "document")]
    [InlineData("configuration", "configuration")]
    [InlineData("Other", "other")]
    [InlineData("Any", "any")]
    public void EvidenceKindIsCanonicalised(string asked, string expected)
    {
        Assert.Equal(expected, CodeQuery.Create("x", evidenceKind: asked).EvidenceKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingEvidenceKindMeansAny(string? asked)
    {
        Assert.Equal(CodeQuery.AnyEvidenceKind, CodeQuery.Create("x", evidenceKind: asked).EvidenceKind);
    }

    [Theory]
    [InlineData("prose")]
    [InlineData("source")]
    [InlineData("codex")]
    public void UnknownEvidenceKindIsRejected(string evidenceKind)
    {
        var exception = Assert.Throws<ArgumentException>(() => CodeQuery.Create("x", evidenceKind: evidenceKind));

        Assert.Contains("evidence_kind must be one of", exception.Message, StringComparison.Ordinal);
        Assert.Contains(evidenceKind, exception.Message, StringComparison.Ordinal);
    }
}
