using Xunit;

namespace SharpCoreAgent.Tests.Languages;

/// <summary>
/// The argument index a call site is on, derived from the text because the checker does not report one.
/// </summary>
public sealed class ActiveParameterFinderTests
{
    [Theory]
    [InlineData("total(items)", 6, 0)]
    [InlineData("total(items, more)", 12, 1)]
    [InlineData("total(a, b, c)", 9, 1)]
    [InlineData("total(a, b, c)", 12, 2)]
    [InlineData("f(g(a, b), c)", 11, 1)]
    [InlineData("f([ 1; 2 ], c)", 13, 1)]
    [InlineData("f(\"a,b\", c)", 9, 1)]
    public void FindCountsTheArgumentsAtTheCallsOwnLevel(string lineText, int column, int expected)
    {
        Assert.Equal(expected, SharpCoreAgent.Languages.FSharp.ActiveParameterFinder.Find(lineText, column));
    }

    [Theory]
    [InlineData("let x = total", 13)]
    [InlineData("", 0)]
    [InlineData("total)", 0)]
    public void FindAnswersMinusOneOutsideACall(string lineText, int column)
    {
        Assert.Equal(-1, SharpCoreAgent.Languages.FSharp.ActiveParameterFinder.Find(lineText, column));
    }

    [Fact]
    public void FindIgnoresCommasInsideClosedNestedCalls()
    {
        // The position is past the inner call, so its comma must not count: f(g(a, b), | here is argument 1.
        const string Line = "f(g(a, b), ";
        Assert.Equal(1, SharpCoreAgent.Languages.FSharp.ActiveParameterFinder.Find(Line, Line.Length));
    }
}
