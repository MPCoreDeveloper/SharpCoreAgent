using System.Globalization;
using System.Security.Cryptography;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Checkpoints;

public sealed partial class TurnCheckpointStore
{
    private sealed class TurnCheckpoint : ITurnCheckpoint
    {
        private readonly TurnCheckpointStore _owner;
        private readonly Dictionary<string, Snapshot> _snapshots;
        private readonly HashSet<string> _restored;
        private readonly object _gate = new();
        private bool _disposed;

        public TurnCheckpoint(TurnCheckpointStore owner, TurnCheckpoint? parent)
        {
            _owner = owner;
            Parent = parent;
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            _snapshots = new Dictionary<string, Snapshot>(comparer);
            _restored = new HashSet<string>(comparer);
        }

        public TurnCheckpoint? Parent { get; }

        public async ValueTask<string?> CaptureAsync(
            string relativePath,
            string fullPath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

            lock (_gate)
            {
                if (_snapshots.ContainsKey(relativePath))
                {
                    return null;
                }
            }

            Snapshot? snapshot;
            try
            {
                snapshot = await SnapshotAsync(relativePath, fullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"could not record '{relativePath}' before changing it: {ex.Message}. Nothing was changed.");
            }

            if (snapshot is null)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{relativePath}' is larger than {MaxCapturedFileBytes / (1024 * 1024)} MiB and cannot be checkpointed. Nothing was changed.");
            }

            lock (_gate)
            {
                _snapshots.TryAdd(relativePath, snapshot);
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Complete(this);
        }

        public bool Seal()
        {
            List<string>? remove = null;
            lock (_gate)
            {
                foreach (var snapshot in _snapshots.Values)
                {
                    snapshot.Post = FingerprintAt(snapshot.FullPath);
                    if (snapshot.Pre.Matches(snapshot.Post))
                    {
                        (remove ??= []).Add(snapshot.RelativePath);
                    }
                }

                if (remove is not null)
                {
                    foreach (var path in remove)
                    {
                        _snapshots.Remove(path);
                        _restored.Remove(path);
                    }
                }

                return _snapshots.Count > 0;
            }
        }

        public void Merge(TurnCheckpoint child)
        {
            Snapshot[] snapshots;
            lock (child._gate)
            {
                snapshots = [.. child._snapshots.Values];
            }

            lock (_gate)
            {
                foreach (var snapshot in snapshots)
                {
                    _snapshots.TryAdd(snapshot.RelativePath, snapshot);
                }
            }
        }

        public CheckpointUndo Restore(CancellationToken cancellationToken)
        {
            var restored = new List<string>();
            var removed = new List<string>();
            var failures = new List<CheckpointFailure>();

            Snapshot[] snapshots;
            lock (_gate)
            {
                snapshots = [.. _snapshots.Values];
            }

            foreach (var snapshot in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (_gate)
                {
                    if (_restored.Contains(snapshot.RelativePath))
                    {
                        continue;
                    }
                }

                var current = FingerprintAt(snapshot.FullPath);
                if (snapshot.Post is null || snapshot.Post.Kind == FingerprintKind.Unknown)
                {
                    failures.Add(new CheckpointFailure(
                        snapshot.RelativePath,
                        snapshot.Post?.Problem ?? "the end-of-turn state was not recorded"));
                    continue;
                }

                if (!snapshot.Post.Matches(current))
                {
                    failures.Add(new CheckpointFailure(
                        snapshot.RelativePath,
                        current.Kind == FingerprintKind.Unknown
                            ? current.Problem ?? "the current file could not be read"
                            : "changed after the turn ended; it was left untouched"));
                    continue;
                }

                try
                {
                    RestoreOne(snapshot);
                    lock (_gate)
                    {
                        _restored.Add(snapshot.RelativePath);
                    }

                    if (snapshot.Pre.Kind == FingerprintKind.Missing)
                    {
                        removed.Add(snapshot.RelativePath);
                    }
                    else
                    {
                        restored.Add(snapshot.RelativePath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    failures.Add(new CheckpointFailure(snapshot.RelativePath, ex.Message));
                }
            }

            return new CheckpointUndo
            {
                HadCheckpoint = true,
                Restored = restored,
                Removed = removed,
                Failures = failures,
            };
        }
        private static async ValueTask<Snapshot?> SnapshotAsync(
            string relativePath,
            string fullPath,
            CancellationToken cancellationToken)
        {
            if (Directory.Exists(fullPath))
            {
                throw new IOException($"'{relativePath}' is a directory, not a file");
            }

            string? link;
            try
            {
                link = new FileInfo(fullPath).LinkTarget;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"'{relativePath}' could not be inspected: {ex.Message}", ex);
            }

            if (link is not null)
            {
                return new Snapshot(relativePath, fullPath, Fingerprint.ForLink(link), null);
            }

            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return new Snapshot(relativePath, fullPath, Fingerprint.ForMissing(), null);
            }

            if (info.Length > MaxCapturedFileBytes)
            {
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return new Snapshot(relativePath, fullPath, Fingerprint.ForMissing(), null);
            }

            if (bytes.LongLength > MaxCapturedFileBytes)
            {
                return null;
            }

            return new Snapshot(
                relativePath,
                fullPath,
                Fingerprint.ForFile(bytes.LongLength, SHA256.HashData(bytes)),
                bytes);
        }

        private static void RestoreOne(Snapshot snapshot)
        {
            var pre = snapshot.Pre;
            if (pre.Kind == FingerprintKind.Missing)
            {
                if (EntryExists(snapshot.FullPath))
                {
                    File.Delete(snapshot.FullPath);
                }

                return;
            }

            if (Directory.Exists(snapshot.FullPath))
            {
                throw new IOException($"'{snapshot.RelativePath}' is now a directory");
            }

            var directory = Path.GetDirectoryName(snapshot.FullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (pre.Kind == FingerprintKind.Link)
            {
                if (EntryExists(snapshot.FullPath))
                {
                    File.Delete(snapshot.FullPath);
                }

                File.CreateSymbolicLink(snapshot.FullPath, pre.LinkTarget!);
                return;
            }

            if (pre.Kind != FingerprintKind.File || snapshot.Bytes is null)
            {
                throw new IOException("the checkpoint has no content for this file");
            }

            var temporary = Path.Combine(
                string.IsNullOrEmpty(directory) ? Path.GetPathRoot(snapshot.FullPath)! : directory,
                string.Create(CultureInfo.InvariantCulture, $".{Path.GetFileName(snapshot.FullPath)}.{Guid.NewGuid():N}.undo.tmp"));
            try
            {
                File.WriteAllBytes(temporary, snapshot.Bytes);
                File.Move(temporary, snapshot.FullPath, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }

        private static Fingerprint FingerprintAt(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    return Fingerprint.Unknown("the path is now a directory");
                }

                var info = new FileInfo(path);
                if (info.LinkTarget is { } link)
                {
                    return Fingerprint.ForLink(link);
                }

                if (!info.Exists)
                {
                    return Fingerprint.ForMissing();
                }

                if (info.Length > MaxCapturedFileBytes)
                {
                    return Fingerprint.Unknown(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"the file is larger than {MaxCapturedFileBytes / (1024 * 1024)} MiB"));
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Fingerprint.ForFile(stream.Length, SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Fingerprint.Unknown(ex.Message);
            }
        }

        private static bool EntryExists(string path) =>
            File.Exists(path) ||
            (new FileInfo(path).LinkTarget is not null);

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary file is harmless; the restore result already says what happened.
            }
        }
    }

    private sealed class Snapshot(string relativePath, string fullPath, Fingerprint pre, byte[]? bytes)
    {
        public string RelativePath { get; } = relativePath;

        public string FullPath { get; } = fullPath;

        public Fingerprint Pre { get; } = pre;

        public byte[]? Bytes { get; } = bytes;

        public Fingerprint? Post { get; set; }
    }

    private sealed record Fingerprint(
        FingerprintKind Kind,
        long Length,
        byte[]? Hash,
        string? LinkTarget,
        string? Problem)
    {
        public static Fingerprint ForMissing() => new(FingerprintKind.Missing, 0, null, null, null);

        public static Fingerprint ForLink(string target) => new(FingerprintKind.Link, 0, null, target, null);

        public static Fingerprint ForFile(long length, byte[] hash) =>
            new(FingerprintKind.File, length, hash, null, null);

        public static Fingerprint Unknown(string problem) =>
            new(FingerprintKind.Unknown, 0, null, null, problem);

        public bool Matches(Fingerprint other) => Kind == other.Kind && Kind switch
        {
            FingerprintKind.Missing => true,
            FingerprintKind.Link => string.Equals(LinkTarget, other.LinkTarget, StringComparison.Ordinal),
            FingerprintKind.File => Length == other.Length &&
                Hash is not null &&
                other.Hash is not null &&
                Hash.AsSpan().SequenceEqual(other.Hash),
            _ => false,
        };
    }

    private enum FingerprintKind
    {
        Missing,
        File,
        Link,
        Unknown,
    }
}
