using System.Globalization;
using SharpCoreAgent.Domain.Code;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>Every way C# code intelligence declines to answer, in one place.</summary>
internal static class CsFailures
{
    public static CodeFailure NoProject() => new()
    {
        Code = "CSH001",
        Message = "No C# project (.csproj) was found in the workspace, so there is nothing to check against.",
        Hint = "Call list_roots to confirm the workspace root, or read_file the project directly.",
    };

    public static CodeFailure NotAssembled(string detail) => new()
    {
        Code = "CSH002",
        Message = detail,
        Hint = "Run dotnet_verify kind=build so the project is restored, then ask again.",
    };

    public static CodeFailure NothingAtPosition(int line, int column) => new()
    {
        Code = "CSH003",
        Message = string.Create(CultureInfo.InvariantCulture, $"Nothing nameable is at {line}:{column}."),
        Hint = "Use find_references or search_code for a name-based answer, or dotnet_verify for compiler errors.",
    };

    public static CodeFailure NotCSharp(string path) => new()
    {
        Code = "CSH004",
        Message = string.Create(CultureInfo.InvariantCulture, $"'{path}' is not a C# source file."),
        Hint = "Use find_references or search_code for other file types, and dotnet_verify for their compiler errors.",
    };

    public static CodeFailure AnalysisFailed(string detail) => new()
    {
        Code = "CSH005",
        Message = detail,
        Hint = "Run dotnet_verify to get the compiler's own answer, or retry once the project builds.",
    };

    public static CodeFailure FileMissing(string path) => new()
    {
        Code = "CSH006",
        Message = string.Create(CultureInfo.InvariantCulture, $"'{path}' does not exist inside the workspace."),
        Hint = "Use list_roots or search_code to find the file you meant.",
    };
}
