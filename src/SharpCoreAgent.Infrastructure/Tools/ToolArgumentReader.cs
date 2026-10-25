using System.Text.Json;
using SharpCoreAgent.Domain.Errors;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Typed, validating accessors over one tool call's argument JSON.
/// </summary>
/// <remarks>
/// Every tool's first act is to read its arguments through this type, which is what makes
/// "validate before you execute" a property of the code rather than a habit. A missing field, a value
/// of the wrong kind and a number outside its documented range all become one
/// <see cref="ToolArgumentException"/> whose message names the parameter and the expected form, so the
/// model reads a correction instead of a stack trace.
/// <para>
/// The reader owns the parsed document; a tool disposes it, and nothing downstream holds a
/// <see cref="JsonElement"/> past that point.
/// </para>
/// </remarks>
public sealed class ToolArgumentReader : IDisposable
{
    private readonly JsonDocument? _document;
    private readonly JsonElement _root;
    private readonly string _toolName;

    private ToolArgumentReader(string toolName, JsonDocument document)
    {
        _toolName = toolName;
        _document = document;
        _root = document.RootElement;
    }

    private ToolArgumentReader(string toolName)
    {
        _toolName = toolName;
        _document = null;
        _root = default;
    }

    /// <summary>
    /// Parses a call's argument JSON.
    /// </summary>
    /// <param name="toolName">The tool name, used in error messages.</param>
    /// <param name="argumentsJson">
    /// The raw JSON object the model produced. Null or blank is treated as <c>{}</c> so a tool whose
    /// arguments are all optional is callable with no arguments at all.
    /// </param>
    /// <returns>A reader positioned at the argument object.</returns>
    /// <exception cref="ArgumentException"><paramref name="toolName"/> is blank.</exception>
    /// <exception cref="ToolArgumentException">The text is not valid JSON, or not a JSON object.</exception>
    public static ToolArgumentReader Parse(string toolName, string? argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new ToolArgumentReader(toolName);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException ex)
        {
            throw new ToolArgumentException(
                toolName,
                string.Empty,
                $"arguments are not valid JSON ({ex.Message})");
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            var kind = Describe(document.RootElement.ValueKind);
            document.Dispose();
            throw new ToolArgumentException(
                toolName,
                string.Empty,
                $"arguments must be a JSON object, not {kind}");
        }

        return new ToolArgumentReader(toolName, document);
    }

    /// <summary>
    /// Reads a required string.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="allowEmpty">
    /// Whether an empty string is a legitimate value. False for paths and patterns, true for a file's
    /// content, where "empty" is a meaningful thing to write.
    /// </param>
    /// <returns>The value, exactly as the model wrote it.</returns>
    /// <exception cref="ToolArgumentException">The parameter is missing, not a string, or empty.</exception>
    public string GetRequiredString(string name, bool allowEmpty = false)
    {
        if (!TryGetValue(name, out var value))
        {
            throw new ToolArgumentException(_toolName, name, "is required");
        }

        var text = AsString(name, value);
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            throw new ToolArgumentException(_toolName, name, "must not be empty");
        }

        return text;
    }

    /// <summary>
    /// Reads an optional string.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <returns>The value, or <see langword="null"/> when the parameter is absent or null.</returns>
    /// <exception cref="ToolArgumentException">The parameter is present but not a string.</exception>
    public string? GetOptionalString(string name) =>
        TryGetValue(name, out var value) ? AsString(name, value) : null;

    /// <summary>
    /// Reads an optional integer, rejecting a value outside its documented range.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="fallback">The value to use when the parameter is absent.</param>
    /// <param name="minimum">The smallest accepted value.</param>
    /// <param name="maximum">The largest accepted value.</param>
    /// <returns>The value, or <paramref name="fallback"/>.</returns>
    /// <exception cref="ToolArgumentException">The parameter is not an integer, or is out of range.</exception>
    public int GetOptionalInt(string name, int fallback, int minimum = int.MinValue, int maximum = int.MaxValue)
    {
        if (!TryGetValue(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw new ToolArgumentException(_toolName, name, $"must be an integer between {minimum} and {maximum}");
        }

        if (number < minimum || number > maximum)
        {
            throw new ToolArgumentException(_toolName, name, $"must be an integer between {minimum} and {maximum}, not {number}");
        }

        return number;
    }

    /// <summary>
    /// Reads an optional boolean.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="fallback">The value to use when the parameter is absent.</param>
    /// <returns>The value, or <paramref name="fallback"/>.</returns>
    /// <exception cref="ToolArgumentException">The parameter is not a boolean.</exception>
    public bool GetOptionalBool(string name, bool fallback)
    {
        if (!TryGetValue(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ToolArgumentException(_toolName, name, "must be true or false"),
        };
    }

    /// <summary>
    /// Determines whether a parameter was supplied with a non-null value.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <returns><see langword="true"/> when the parameter is present.</returns>
    public bool Has(string name) => TryGetValue(name, out _);

    /// <inheritdoc />
    public void Dispose() => _document?.Dispose();

    private bool TryGetValue(string name, out JsonElement value)
    {
        if (_root.ValueKind != JsonValueKind.Object ||
            !_root.TryGetProperty(name, out value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            value = default;
            return false;
        }

        return true;
    }

    private string AsString(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ToolArgumentException(_toolName, name, $"must be a string, not {Describe(value.ValueKind)}");
        }

        return value.GetString() ?? string.Empty;
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "an absent value",
    };
}
