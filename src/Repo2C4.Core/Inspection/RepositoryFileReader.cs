using System.Security;
using System.Text;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.Inspection;

/// <summary>
/// Bounded, best-effort reading of inventoried files. The caller must authorize a stable checkout:
/// managed reparse-point checks cannot atomically defeat a concurrent hostile path-replacement race.
/// </summary>
internal static class RepositoryFileReader
{
    internal const int MaxExtractedFileBytes = 524_288;

    internal static (string? Content, string? ErrorCode) Read(
        string root,
        RepositoryFile inventoryFile,
        RepositoryScanOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ContractValidator.IsNormalizedRelativePath(inventoryFile.RelativePath)
            || inventoryFile.SizeBytes > MaxExtractedFileBytes
            || inventoryFile.SizeBytes > options.MaxBytesPerFile)
        {
            return (null, "extract.fileTooLarge");
        }

        string file = Path.GetFullPath(Path.Combine(
            root,
            inventoryFile.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!ContractValidator.IsNormalizedRelativePath(
            Path.GetRelativePath(root, file).Replace('\\', '/')))
        {
            return (null, "extract.outsideRoot");
        }

        try
        {
            if (!SafeComponents(root, inventoryFile.RelativePath, cancellationToken))
            {
                return (null, "extract.link");
            }

            if (!OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(file)
                    & (UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0)
            {
                return (null, "extract.accessDenied");
            }

            using FileStream stream = new(
                file, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4_096, FileOptions.SequentialScan);

            if (!SafeComponents(root, inventoryFile.RelativePath, cancellationToken))
            {
                return (null, "extract.link");
            }

            if (stream.Length != inventoryFile.SizeBytes || stream.Length > MaxExtractedFileBytes)
            {
                return (null, "extract.fileChanged");
            }

            byte[] bytes = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    return (null, "extract.fileChanged");
                }

                offset += read;
            }

            if (stream.Length != inventoryFile.SizeBytes
                || !SafeComponents(root, inventoryFile.RelativePath, cancellationToken))
            {
                return (null, "extract.fileChanged");
            }

            if (bytes.AsSpan().Contains((byte)0))
            {
                return (null, "extract.binary");
            }

            // Invalid UTF-8 and binary content are not interpreted as configuration or code.
            string content = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            return (content, null);
        }
        catch (DecoderFallbackException)
        {
            return (null, "extract.binary");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SecurityException or ArgumentException or NotSupportedException)
        {
            // Never include exception.Message, absolute paths, source text or tokens in diagnostics.
            return (null, exception is UnauthorizedAccessException or SecurityException
                ? "extract.accessDenied"
                : "extract.unavailable");
        }
    }

    private static bool SafeComponents(string root, string relative, CancellationToken cancellationToken)
    {
        string current = root;
        if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        foreach (string segment in relative.Split('/'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }
        }

        return true;
    }
}
