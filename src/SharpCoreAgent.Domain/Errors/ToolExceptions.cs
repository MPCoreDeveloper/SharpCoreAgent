#pragma warning disable CA1032 // Exception type carries mandatory context; see SharpCoreAgentException.cs.

using System.Globalization;

namespace SharpCoreAgent.Domain.Errors;

/// <summary>
/// A tool was invoked under a name the registry does not know.
/// </summary>
public sealed class ToolNotFoundException : SharpCoreAgentException
{
    /// <summary>Creates an exception.</summary>
    /// <param name="toolName">The unknown name.</param>
    public ToolNotFoundException(string toolName)
        : base(string.Create(CultureInfo.InvariantCulture, $"No tool named '{toolName}' is registered."))
    {
        ToolName = toolName;
    }

    /// <summary>Gets the unknown name.</summary>
    public string ToolName { get; }
}

/// <summary>
/// A tool's arguments could not be parsed or did not satisfy its schema.
/// </summary>
/// <remarks>
/// Tools normally turn this into a readable <c>ToolResult</c> failure rather than throwing; the type
/// exists so argument validation has one vocabulary and one message format.
/// </remarks>
public sealed class ToolArgumentException : SharpCoreAgentException
{
    /// <summary>Creates an exception.</summary>
    /// <param name="toolName">The tool.</param>
    /// <param name="parameter">The offending parameter, or the empty string when the JSON itself is malformed.</param>
    /// <param name="message">What is wrong.</param>
    public ToolArgumentException(string toolName, string parameter, string message)
        : base(string.IsNullOrEmpty(parameter)
            ? string.Create(CultureInfo.InvariantCulture, $"{toolName}: {message}")
            : string.Create(CultureInfo.InvariantCulture, $"{toolName}: parameter '{parameter}' {message}"))
    {
        ToolName = toolName;
        Parameter = parameter;
    }

    /// <summary>Gets the tool.</summary>
    public string ToolName { get; }

    /// <summary>Gets the offending parameter, or the empty string.</summary>
    public string Parameter { get; }
}
