namespace SharpCoreAgent.Domain.Tools;

/// <summary>
/// The names of the tools the turn loop itself reasons about, kept apart from the tools that implement them.
/// </summary>
/// <remarks>
/// A tool's name is normally its own business: it lives on the tool, in the prompt that describes it and in the
/// schema the model is shown. The verifier is the exception, because the loop refuses to end a turn on the
/// model's word until one has passed, and the loop lives in <c>SharpCoreAgent.Application</c> — which must not
/// reference the infrastructure that implements the tool. One constant keeps the gate and the tool from
/// disagreeing about the string that decides whether a turn may end.
/// </remarks>
public static class ToolNames
{
    /// <summary>Gets the name of the tool that builds or tests a .NET workspace and reports the result.</summary>
    public const string DotnetVerify = "dotnet_verify";
}
