using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Repo2C4.Core.LikeC4;

namespace Repo2C4.Core.Generation;

public enum GeneratedFileChangeKind
{
    Unchanged,
    Added,
    Modified,
    Conflict,
}

public sealed record GeneratedFileChange(
    string FileName,
    GeneratedFileChangeKind Kind,
    string? PreviousHash,
    string NewHash);

public sealed record GenerationManifestEntry(string FileName, string Sha256);

public sealed record GenerationManifest(
    string SchemaVersion,
    string ModelSchemaVersion,
    GenerationManifestEntry[] Files);

public sealed record GenerationPlan(
    string ManifestFileName,
    GeneratedFileChange[] Changes,
    bool HasConflicts,
    bool HasChanges);

public static class ManagedOutputManager
{
    public const string ManifestFileName = ".repo2c4-manifest.json";
    private const string ManifestSchemaVersion = "1.0";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<GenerationPlan> PreviewAsync(
        string outputRoot,
        string modelSchemaVersion,
        IReadOnlyList<LikeC4GeneratedFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelSchemaVersion);
        ArgumentNullException.ThrowIfNull(files);

        string root = Path.GetFullPath(outputRoot);
        GenerationManifest? manifest = await TryReadManifestAsync(root, cancellationToken).ConfigureAwait(false);
        Dictionary<string, GenerationManifestEntry> prior = manifest?.Files.ToDictionary(
            item => item.FileName,
            StringComparer.Ordinal) ?? new Dictionary<string, GenerationManifestEntry>(StringComparer.Ordinal);

        List<GeneratedFileChange> changes = [];
        foreach (LikeC4GeneratedFile file in files.OrderBy(item => item.FileName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateFileName(file.FileName);
            string target = ResolveTarget(root, file.FileName);
            string newHash = ComputeHash(file.Content);

            if (Directory.Exists(target))
            {
                changes.Add(new GeneratedFileChange(
                    file.FileName,
                    GeneratedFileChangeKind.Conflict,
                    prior.TryGetValue(file.FileName, out GenerationManifestEntry? directory) ? directory.Sha256 : null,
                    newHash));
                continue;
            }

            if (!File.Exists(target))
            {
                changes.Add(new GeneratedFileChange(
                    file.FileName,
                    prior.ContainsKey(file.FileName) ? GeneratedFileChangeKind.Conflict : GeneratedFileChangeKind.Added,
                    prior.TryGetValue(file.FileName, out GenerationManifestEntry? missing) ? missing.Sha256 : null,
                    newHash));
                continue;
            }

            if (IsReparsePoint(target))
            {
                changes.Add(new GeneratedFileChange(
                    file.FileName,
                    GeneratedFileChangeKind.Conflict,
                    prior.TryGetValue(file.FileName, out GenerationManifestEntry? linked) ? linked.Sha256 : null,
                    newHash));
                continue;
            }

            string current = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
            string currentHash = ComputeHash(current);

            if (!prior.TryGetValue(file.FileName, out GenerationManifestEntry? entry))
            {
                changes.Add(new GeneratedFileChange(file.FileName, GeneratedFileChangeKind.Conflict, null, newHash));
                continue;
            }

            if (!string.Equals(currentHash, entry.Sha256, StringComparison.Ordinal))
            {
                changes.Add(new GeneratedFileChange(file.FileName, GeneratedFileChangeKind.Conflict, entry.Sha256, newHash));
                continue;
            }

            GeneratedFileChangeKind kind = string.Equals(currentHash, newHash, StringComparison.Ordinal)
                ? GeneratedFileChangeKind.Unchanged
                : GeneratedFileChangeKind.Modified;
            changes.Add(new GeneratedFileChange(file.FileName, kind, entry.Sha256, newHash));
        }

        return new GenerationPlan(
            ManifestFileName,
            [.. changes],
            changes.Any(item => item.Kind == GeneratedFileChangeKind.Conflict),
            changes.Any(item => item.Kind is GeneratedFileChangeKind.Added or GeneratedFileChangeKind.Modified));
    }

    public static async Task CommitAsync(
        string outputRoot,
        string modelSchemaVersion,
        IReadOnlyList<LikeC4GeneratedFile> files,
        GenerationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.HasConflicts)
        {
            throw new IOException("managed_output_conflict");
        }

        string root = Path.GetFullPath(outputRoot);
        GenerationPlan currentPlan = await PreviewAsync(
            root,
            modelSchemaVersion,
            files,
            cancellationToken).ConfigureAwait(false);
        if (currentPlan.HasConflicts)
        {
            throw new IOException("managed_output_conflict");
        }

        Directory.CreateDirectory(root);
        if (IsReparsePoint(root))
        {
            throw new IOException("managed_output_linked_root");
        }

        string transactionRoot = Path.Combine(root, ".repo2c4-txn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transactionRoot);
        List<(string Temp, string Target, string? Backup)> prepared = [];

        try
        {
            foreach (LikeC4GeneratedFile file in files.OrderBy(item => item.FileName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateFileName(file.FileName);
                string target = ResolveTarget(root, file.FileName);
                string temp = Path.Combine(transactionRoot, file.FileName + ".tmp");
                string? backup = File.Exists(target) ? Path.Combine(transactionRoot, file.FileName + ".bak") : null;
                await File.WriteAllTextAsync(temp, file.Content, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
                if (backup is not null)
                {
                    File.Copy(target, backup, overwrite: false);
                }

                prepared.Add((temp, target, backup));
            }

            GenerationManifest manifest = new(
                ManifestSchemaVersion,
                modelSchemaVersion,
                [.. files.OrderBy(item => item.FileName, StringComparer.Ordinal)
                    .Select(file => new GenerationManifestEntry(file.FileName, ComputeHash(file.Content)))]);
            string manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions) + Environment.NewLine;
            string manifestTarget = ResolveTarget(root, ManifestFileName);
            string manifestTemp = Path.Combine(transactionRoot, ManifestFileName + ".tmp");
            string? manifestBackup = File.Exists(manifestTarget)
                ? Path.Combine(transactionRoot, ManifestFileName + ".bak")
                : null;
            await File.WriteAllTextAsync(manifestTemp, manifestJson, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            if (manifestBackup is not null)
            {
                File.Copy(manifestTarget, manifestBackup, overwrite: false);
            }

            prepared.Add((manifestTemp, manifestTarget, manifestBackup));

            List<(string Target, string? Backup)> committed = [];
            try
            {
                foreach ((string temp, string target, string? backup) in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(target))
                    {
                        File.Move(temp, target, overwrite: true);
                    }
                    else
                    {
                        File.Move(temp, target);
                    }
                    committed.Add((target, backup));
                }
            }
            catch
            {
                foreach ((string target, string? backup) in committed.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (File.Exists(target))
                        {
                            File.Delete(target);
                        }

                        if (backup is not null && File.Exists(backup))
                        {
                            File.Copy(backup, target, overwrite: true);
                        }
                    }
                    catch
                    {
                    }
                }

                throw;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(transactionRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<GenerationManifest?> TryReadManifestAsync(
        string root,
        CancellationToken cancellationToken)
    {
        string path = ResolveTarget(root, ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        if (IsReparsePoint(path))
        {
            throw new IOException("managed_output_manifest_link");
        }

        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        GenerationManifest? manifest = JsonSerializer.Deserialize<GenerationManifest>(json);
        if (manifest is null || manifest.SchemaVersion != ManifestSchemaVersion)
        {
            throw new IOException("managed_output_manifest_invalid");
        }

        foreach (GenerationManifestEntry entry in manifest.Files)
        {
            ValidateFileName(entry.FileName);
        }

        return manifest;
    }

    private static string ResolveTarget(string root, string fileName)
    {
        string target = Path.GetFullPath(Path.Combine(root, fileName));
        string? parent = Path.GetDirectoryName(target);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(parent, root, comparison))
        {
            throw new IOException("managed_output_path_escape");
        }

        return target;
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || fileName is "." or "..")
        {
            throw new IOException("managed_output_invalid_name");
        }
    }

    private static string ComputeHash(string content)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static bool IsReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.ReparsePoint) != 0;
    }
}
