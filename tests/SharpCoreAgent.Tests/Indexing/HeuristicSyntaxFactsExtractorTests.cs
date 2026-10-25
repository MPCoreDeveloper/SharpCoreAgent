using SharpCoreAgent.Indexing.Extraction;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The extractor is a heuristic, so these tests pin the reading it is allowed to have: which declarations exist,
/// what a range covers, that a relation comes from code and never from prose, and that two runs agree.
/// </summary>
public sealed class HeuristicSyntaxFactsExtractorTests
{
    private const string ShapeSource = """
        using System;
        using Demo.Geometry.Contracts;

        namespace Demo.Geometry;

        /// <summary>
        /// The base of every shape in the demo corpus; see <see cref="IShape"/>.
        /// </summary>
        public abstract class Shape
        {
            /// <summary>Gets the shape's area.</summary>
            public abstract double Area { get; }

            public string Name { get; init; } = "shape";

            protected void Remember(string note) => Trace(note);

            private void Trace(string note) => Console.WriteLine(note);
        }
        """;

    private const string CircleSource = """
        namespace Demo.Geometry;

        public sealed class Circle : Shape, IShape
        {
            private const double Tolerance = 0.0001;
            private readonly double _radius;

            public Circle(double radius)
            {
                _radius = radius;
            }

            /// <summary>Scale of the circle; see <see cref="Shape"/>.</summary>
            public double Scale(int factor)
            {
                return Compute(_radius) + Compute(_radius) + Compute(_radius)
                     + Compute(_radius) + Compute(_radius) + Compute(_radius) + Compute(_radius);
            }

            private double Compute(double value) => value * 2;

            // A comment mentioning Ignored(); and RefreshAsync( is not code.
            public string Describe() => $"a circle calling Ignored() with {_radius}";
        }
        """;

    private const string ReadmeSource = """
        # Geometry

        The geometry module.

        ## Circle

        A circle is defined by its radius.

        ```csharp
        # not a heading: a comment inside a fence
        var circle = new Circle(1);
        ```

        ## Shape

        The base class.
        """;

    private const string PythonSource = """
        import os
        from demo.geometry import shapes

        class Circle(Shape):
            def area(self, precision=2):
                return compute(self.radius)

            def describe(self):
                return "circle"
        """;

    [Fact]
    public void FindsDeclarationsWithStableKeysAndRanges()
    {
        var facts = Extract("src/Geometry/Shape.cs", "csharp", ShapeSource);

        Assert.Equal(
            [
                "Demo.Geometry.Shape#class",
                "Demo.Geometry.Shape.Area#property",
                "Demo.Geometry.Shape.Name#property",
                "Demo.Geometry.Shape.Remember#method",
                "Demo.Geometry.Shape.Trace#method",
            ],
            facts.Symbols.Select(symbol => symbol.Key));

        var shape = facts.Symbols[0];

        Assert.Equal("Shape", shape.Name);
        Assert.Equal("Demo.Geometry.Shape", shape.QualifiedName);
        Assert.Equal("Demo.Geometry", shape.Namespace);
        Assert.Equal(9, shape.StartLine);
        Assert.Equal(19, shape.EndLine);
        Assert.Equal("public abstract class Shape", shape.Signature);

        var area = facts.Symbols[1];
        Assert.Equal(SymbolKinds.Property, area.Kind);
        Assert.Equal("Demo.Geometry.Shape.Area", area.QualifiedName);
        Assert.Equal(12, area.StartLine);
        Assert.Equal(12, area.EndLine);
    }

    [Fact]
    public void ReadsSummariesAndTheSymbolsTheyMention()
    {
        var facts = Extract("src/Geometry/Shape.cs", "csharp", ShapeSource);

        var shape = facts.Symbols[0];
        Assert.Equal("The base of every shape in the demo corpus; see .", shape.Doc);

        var mention = Assert.Single(facts.Relations, relation => relation.Kind == RelationKinds.DocMentions);
        Assert.Equal("Demo.Geometry.Shape#class", mention.Source);
        Assert.Equal("IShape", mention.Target);
    }

    [Fact]
    public void RecordsInheritanceAndImplementation()
    {
        var facts = Extract("src/Geometry/Circle.cs", "csharp", CircleSource);

        var inherits = Assert.Single(facts.Relations, relation => relation.Kind == RelationKinds.Inherits);
        Assert.Equal("Demo.Geometry.Circle#class", inherits.Source);
        Assert.Equal("Shape", inherits.Target);

        var implements = Assert.Single(facts.Relations, relation => relation.Kind == RelationKinds.Implements);
        Assert.Equal("IShape", implements.Target);
    }

    [Fact]
    public void CompressesCallSitesAtTheCap()
    {
        var facts = Extract("src/Geometry/Circle.cs", "csharp", CircleSource);

        var call = Assert.Single(
            facts.Relations,
            relation => relation.Kind == RelationKinds.Calls && relation.Target == "Compute");

        Assert.Equal("Demo.Geometry.Circle.Scale#method", call.Source);
        Assert.Equal(HeuristicSyntaxFactsExtractor.MaxCallWeight, call.Weight);
    }

    [Fact]
    public void IgnoresCallsInCommentsAndStrings()
    {
        var facts = Extract("src/Geometry/Circle.cs", "csharp", CircleSource);

        Assert.DoesNotContain(facts.Relations, relation => relation.Target is "Ignored" or "RefreshAsync");
        Assert.DoesNotContain(facts.Symbols, symbol => symbol.Name is "Ignored" or "RefreshAsync");
    }

    [Fact]
    public void SplitsMarkdownIntoHeadedSections()
    {
        var facts = Extract("docs/geometry.md", "markdown", ReadmeSource);

        Assert.Empty(facts.Symbols);
        Assert.Equal(["Geometry", "Circle", "Shape"], facts.Docs.Select(doc => doc.Heading));

        var circle = facts.Docs[1];
        Assert.Equal(5, circle.StartLine);
        Assert.Equal(12, circle.EndLine);
        Assert.Contains("A circle is defined by its radius.", circle.Text, StringComparison.Ordinal);
        Assert.Contains("```csharp", circle.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsPythonDeclarationsAndImports()
    {
        var facts = Extract("tools/circle.py", "python", PythonSource);

        Assert.Equal(["os", "demo.geometry"], facts.Imports);
        Assert.Equal(
            ["Circle#class", "Circle.area#method", "Circle.describe#method"],
            facts.Symbols.Select(symbol => symbol.Key));

        Assert.Equal(4, facts.Symbols[0].StartLine);
        Assert.Equal(9, facts.Symbols[0].EndLine);
        Assert.Equal(5, facts.Symbols[1].StartLine);
        Assert.Equal(6, facts.Symbols[1].EndLine);

        Assert.Contains(
            facts.Relations,
            relation => relation.Kind == RelationKinds.Inherits && relation.Target == "Shape");
        Assert.Contains(
            facts.Relations,
            relation => relation.Kind == RelationKinds.Calls && relation.Target == "compute");
    }

    [Fact]
    public void ProducesTheSameFactsEveryTime()
    {
        var first = Extract("src/Geometry/Circle.cs", "csharp", CircleSource);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var next = Extract("src/Geometry/Circle.cs", "csharp", CircleSource);

            Assert.Equal(first.Symbols.Select(symbol => symbol.Key), next.Symbols.Select(symbol => symbol.Key));
            Assert.Equal(
                first.Relations.Select(static relation =>
                    (relation.Source, relation.Target, relation.Kind, relation.Weight)),
                next.Relations.Select(static relation =>
                    (relation.Source, relation.Target, relation.Kind, relation.Weight)));
        }
    }

    [Fact]
    public void AnswersWithNothingForAFileWithoutCode()
    {
        var empty = Extract("notes.txt", "text", string.Empty);

        Assert.Empty(empty.Symbols);
        Assert.Empty(empty.Relations);
        Assert.Equal(HeuristicSyntaxFactsExtractor.ExtractorId, empty.Extractor);

        var json = Extract("data.json", "json", "{ \"a\": 1 }");

        Assert.Empty(json.Symbols);
        Assert.Equal(HeuristicSyntaxFactsExtractor.ExtractorId, json.Extractor);
    }

    private static SyntaxFacts Extract(string path, string language, string text) =>
        HeuristicSyntaxFactsExtractor.Create().Extract(
            new ScannedFile
            {
                Path = path,
                FullPath = "C:/workspace/" + path,
                Language = language,
                SizeBytes = text.Length,
                ModifiedUtc = DateTimeOffset.UnixEpoch,
            },
            text,
            TestContext.Current.CancellationToken);
}
