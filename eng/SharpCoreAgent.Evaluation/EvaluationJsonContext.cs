using System.Text.Json.Serialization;

namespace SharpCoreAgent.Evaluation;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EvaluationScenario))]
[JsonSerializable(typeof(EvaluationReport))]
internal sealed partial class EvaluationJsonContext : JsonSerializerContext;
