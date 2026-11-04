using System.Security.Cryptography;
using System.Text;
using SharpCoreAgent.Domain.Code;
namespace SharpCoreAgent.Languages.CSharp;

/// <summary>Loads a project graph and caches its last Roslyn solution until an input stamp changes.</summary>
internal sealed class CsSolutionCache(CsProjectSource source)
{
    private readonly Lock _gate = new();
    private CsSolutionSnapshot? _current;

    public CsSnapshotLoad Get(CsProjectModel target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var models = new Dictionary<string, CsProjectModel>(StringComparer.OrdinalIgnoreCase)
            {
                [target.ProjectFile] = target,
            };
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.ProjectFile };
            if (LoadReferences(target, models, visiting, cancellationToken) is { } failure)
            {
                return CsSnapshotLoad.Failed(failure);
            }

            var stamp = Stamp(models);
            if (_current is { } current
                && string.Equals(current.Stamp, stamp, StringComparison.Ordinal)
                && current.Models.ContainsKey(target.ProjectFile))
            {
                return CsSnapshotLoad.Of(current);
            }

            var built = CsSolutionBuilder.Build(models, stamp, target.ProjectFile, cancellationToken);
            if (built.Snapshot is null)
            {
                return CsSnapshotLoad.Failed(built.Failure!);
            }

            _current?.Workspace.Dispose();
            _current = built.Snapshot;
            return CsSnapshotLoad.Of(built.Snapshot);
        }
    }

    private CodeFailure? LoadReferences(
        CsProjectModel model,
        Dictionary<string, CsProjectModel> models,
        HashSet<string> visiting,
        CancellationToken cancellationToken)
    {
        foreach (var reference in model.ProjectReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (models.ContainsKey(reference))
            {
                continue;
            }

            if (!visiting.Add(reference))
            {
                return CsFailures.NotAssembled($"The C# project graph contains a cycle through '{Path.GetFileName(reference)}'.");
            }

            var load = source.Load(reference, cancellationToken);
            if (load.Project is not { } child)
            {
                return load.Failure;
            }

            models[reference] = child;
            if (LoadReferences(child, models, visiting, cancellationToken) is { } failure)
            {
                return failure;
            }
        }

        return null;
    }

    private static string Stamp(IReadOnlyDictionary<string, CsProjectModel> models)
    {
        var builder = new StringBuilder();
        foreach (var model in models.Values.OrderBy(model => model.ProjectFile, StringComparer.Ordinal))
        {
            builder.Append(model.ProjectFile).Append('|').Append(model.InputStamp).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
