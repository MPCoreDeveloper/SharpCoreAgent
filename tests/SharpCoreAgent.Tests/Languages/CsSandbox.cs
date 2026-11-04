using System.Globalization;
using SharpCoreAgent.Tests.Tools;

namespace SharpCoreAgent.Tests.Languages;

/// <summary>A two-project C# fixture with no packages, so no restore or network is needed.</summary>
internal static class CsSandbox
{
    public const string CartSource = """
        namespace Fixture;

        public static class Cart
        {
            public static decimal Total(decimal[] prices) => prices.Sum();

            public static decimal Total(decimal[] prices, decimal tax) => prices.Sum() + tax;
        }

        public sealed class Item
        {
            public Item(string name, decimal price)
            {
                Name = name;
                Price = price;
            }

            public string Name { get; }

            public decimal Price { get; }
        }

        public static class Ledger
        {
            public static decimal Total(decimal[] values) => values.Length;
        }
        """;

    public const string ProgramSource = """
        using Fixture;

        var prices = new[] { 1.5m, 2.5m };
        Console.WriteLine(Cart.Total(prices));
        Console.WriteLine(Cart.Total(prices, tax: 1m));
        Console.WriteLine(Ledger.Total(prices));
        Console.WriteLine(Subtotal(prices));
        // Subtotal appears in this comment and must not be a use.
        var item = new Item("widget", 1m);
        Console.WriteLine(item.Name);
        """;

    public const string PropsSource = """
        #if !FROM_DIRECTORY_PROPS || !FROM_DIRECTORY_TARGETS
        #error Directory.Build properties were not read.
        #endif

        namespace Fixture;

        internal static class PropsCheck
        {
        }
        """;

    public static ToolTestWorkspace Create()
    {
        var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("Directory.Build.props", """
            <Project>
              <PropertyGroup><DefineConstants>FROM_DIRECTORY_PROPS</DefineConstants></PropertyGroup>
            </Project>
            """);
        sandbox.Write("Directory.Build.targets", """
            <Project>
              <PropertyGroup><DefineConstants>$(DefineConstants);FROM_DIRECTORY_TARGETS</DefineConstants></PropertyGroup>
            </Project>
            """);
        sandbox.Write("Cart/Cart.cs", CartSource);
        sandbox.Write("Cart/PropsCheck.cs", PropsSource);
        sandbox.Write("Cart/Excluded.cs", "this file is removed from the compilation and is not C#");
        sandbox.Write("Cart/Cart.csproj", Project("classlib", """<Compile Remove="Excluded.cs" />"""));
        sandbox.Write("App/Program.cs", ProgramSource);
        sandbox.Write("App/App.csproj", Project("exe", """<ProjectReference Include="..\Cart\Cart.csproj" />"""));
        return sandbox;
    }

    public static (int Line, int Column) PositionOf(string source, string token)
    {
        var index = source.IndexOf(token, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"'{token}' does not appear in the fixture source."));
        }

        var line = 1;
        var column = 0;
        for (var position = 0; position < index; position++)
        {
            if (source[position] == '\n')
            {
                line++;
                column = 0;
            }
            else if (source[position] != '\r')
            {
                column++;
            }
        }

        return (line, column);
    }

    private static string Project(string outputType, string? item) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net{{Environment.Version.Major}}.0</TargetFramework>
            <OutputType>{{outputType}}</OutputType>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          {{(item is null ? string.Empty : $"<ItemGroup>{item}</ItemGroup>")}}
        </Project>
        """;
}
