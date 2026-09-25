using System.Security.Cryptography;
using System.Text;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.Inspection;
using Repo2C4.Core.LikeC4;

namespace Repo2C4.Cli;

public static class CliExitCodes
{
    public const int Success = 0;
    public const int UsageError = 2;
    public const int InvalidData = 3;
    public const int ValidationFailed = 4;
    public const int IoError = 5;
}

internal static class CliApplication
{
    private const long MaxModelBytes = 4L * 1024 * 1024;

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h" or "help")
        {
            WriteHelp(standardOutput);
            return CliExitCodes.Success;
        }

        if (args.Length == 0)
        {
            standardError.WriteLine("A command is required. Run --help for usage.");
            return CliExitCodes.UsageError;
        }

        string command = args[0];
        string[] commandArguments = args[1..];

        if (commandArguments.Length == 1 && commandArguments[0] is "--help" or "-h")
        {
            return WriteCommandHelp(command, standardOutput, standardError);
        }

        return command switch
        {
            "inspect" => await RunInspectAsync(commandArguments, standardOutput, standardError, cancellationToken)
                .ConfigureAwait(false),
            "generate" => await RunGenerateAsync(commandArguments, standardOutput, standardError, cancellationToken)
                .ConfigureAwait(false),
            "validate" => await RunValidateAsync(commandArguments, standardOutput, standardError, cancellationToken)
                .ConfigureAwait(false),
            _ => UnknownCommand(command, standardError),
        };
    }

    private static async Task<int> RunInspectAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(
            args,
            ["--repository", "--output"],
            [],
            out Dictionary<string, string> values,
            out _,
            out string? parseError))
        {
            standardError.WriteLine(parseError);
            return CliExitCodes.UsageError;
        }

        if (!TryGetRequired(values, "--repository", out string repository)
            || !TryGetRequired(values, "--output", out string output))
        {
            standardError.WriteLine("inspect requires --repository PATH and --output FILE.");
            return CliExitCodes.UsageError;
        }

        try
        {
            string fullRepository = Path.GetFullPath(repository);
            if (!Directory.Exists(fullRepository))
            {
                standardError.WriteLine("Repository directory does not exist.");
                return CliExitCodes.IoError;
            }

            string fullOutput = Path.GetFullPath(output);
            string? outputDirectory = Path.GetDirectoryName(fullOutput);
            if (outputDirectory is null)
            {
                standardError.WriteLine("Output file path is invalid.");
                return CliExitCodes.UsageError;
            }

            Directory.CreateDirectory(outputDirectory);
            if (File.Exists(fullOutput))
            {
                standardError.WriteLine("Snapshot output already exists; choose a new path.");
                return CliExitCodes.IoError;
            }

            RepositoryScanOptions options = new(fullRepository, CreateRepositoryId(fullRepository));
            RepositorySnapshot snapshot = RepositoryFactExtractor.Extract(options, cancellationToken);
            string json = NormalizeText(ContractJson.SerializeSnapshot(snapshot));

            await File.WriteAllTextAsync(fullOutput, json, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            standardOutput.WriteLine(fullOutput);
            return CliExitCodes.Success;
        }
        catch (ContractValidationException exception)
        {
            WriteContractErrors(exception, standardError);
            return CliExitCodes.InvalidData;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException)
        {
            standardError.WriteLine("Inspection failed because a local path could not be read or written.");
            return CliExitCodes.IoError;
        }
    }

    private static async Task<int> RunGenerateAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(
            args,
            ["--model", "--output"],
            ["--overwrite"],
            out Dictionary<string, string> values,
            out HashSet<string> flags,
            out string? parseError))
        {
            standardError.WriteLine(parseError);
            return CliExitCodes.UsageError;
        }

        if (!TryGetRequired(values, "--model", out string modelPath)
            || !TryGetRequired(values, "--output", out string outputPath))
        {
            standardError.WriteLine("generate requires --model FILE and --output DIR.");
            return CliExitCodes.UsageError;
        }

        try
        {
            string fullModelPath = Path.GetFullPath(modelPath);
            if (!File.Exists(fullModelPath))
            {
                standardError.WriteLine("Architecture model file does not exist.");
                return CliExitCodes.IoError;
            }

            FileInfo modelInfo = new(fullModelPath);
            if (modelInfo.Length > MaxModelBytes)
            {
                standardError.WriteLine("Architecture model exceeds the 4 MiB CLI input limit.");
                return CliExitCodes.InvalidData;
            }

            string json = await File.ReadAllTextAsync(fullModelPath, cancellationToken).ConfigureAwait(false);
            ArchitectureModel model = ContractJson.DeserializeModel(json);
            IReadOnlyList<LikeC4GeneratedFile> files = LikeC4Emitter.Emit(model);

            string outputRoot = Path.GetFullPath(outputPath);
            if (Directory.Exists(outputRoot) && IsReparsePoint(outputRoot))
            {
                standardError.WriteLine("Output directory must not be a symlink, junction or reparse point.");
                return CliExitCodes.IoError;
            }

            Directory.CreateDirectory(outputRoot);
            if (IsReparsePoint(outputRoot))
            {
                standardError.WriteLine("Output directory must not be a symlink, junction or reparse point.");
                return CliExitCodes.IoError;
            }

            bool overwrite = flags.Contains("--overwrite");
            List<(LikeC4GeneratedFile File, string Target)> targets = [];
            foreach (LikeC4GeneratedFile file in files)
            {
                string target = Path.GetFullPath(Path.Combine(outputRoot, file.FileName));
                string? targetDirectory = Path.GetDirectoryName(target);
                if (!string.Equals(targetDirectory, outputRoot, StringComparison.Ordinal))
                {
                    standardError.WriteLine("Generated file path escaped the authorized output directory.");
                    return CliExitCodes.IoError;
                }

                if (File.Exists(target))
                {
                    if (IsReparsePoint(target))
                    {
                        standardError.WriteLine("Existing output file must not be a symlink or reparse point.");
                        return CliExitCodes.IoError;
                    }

                    if (!overwrite)
                    {
                        standardError.WriteLine(
                            "Output file already exists: " + file.FileName + ". Re-run with --overwrite to replace it.");
                        return CliExitCodes.IoError;
                    }
                }

                targets.Add((file, target));
            }

            foreach ((LikeC4GeneratedFile file, string target) in targets)
            {
                await File.WriteAllTextAsync(
                    target,
                    NormalizeText(file.Content),
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
                standardOutput.WriteLine(target);
            }

            return CliExitCodes.Success;
        }
        catch (ContractValidationException exception)
        {
            WriteContractErrors(exception, standardError);
            return CliExitCodes.InvalidData;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException)
        {
            standardError.WriteLine("Generation failed because a local path could not be read or written.");
            return CliExitCodes.IoError;
        }
    }

    private static async Task<int> RunValidateAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(
            args,
            ["--output"],
            [],
            out Dictionary<string, string> values,
            out _,
            out string? parseError))
        {
            standardError.WriteLine(parseError);
            return CliExitCodes.UsageError;
        }

        if (!TryGetRequired(values, "--output", out string output))
        {
            standardError.WriteLine("validate requires --output DIR.");
            return CliExitCodes.UsageError;
        }

        try
        {
            string outputRoot = Path.GetFullPath(output);
            LikeC4ValidationResult result =
                await LikeC4CliValidator.ValidateAsync(outputRoot, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            if (result.IsValid)
            {
                standardOutput.WriteLine("LikeC4 validation succeeded.");
                return CliExitCodes.Success;
            }

            standardError.WriteLine("LikeC4 validation failed (tool exit " + result.ExitCode + ").");
            foreach (LikeC4ValidationDiagnostic diagnostic in result.Diagnostics)
            {
                string location = diagnostic.RelativePath is null ? string.Empty : " [" + diagnostic.RelativePath + "]";
                standardError.WriteLine(diagnostic.Code + location + ": " + diagnostic.Message);
            }

            return CliExitCodes.ValidationFailed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException)
        {
            standardError.WriteLine("Validation failed because the output path could not be accessed.");
            return CliExitCodes.IoError;
        }
    }

    private static bool TryParseOptions(
        string[] args,
        string[] valueOptions,
        string[] flagOptions,
        out Dictionary<string, string> values,
        out HashSet<string> flags,
        out string? error)
    {
        HashSet<string> allowedValues = new(valueOptions, StringComparer.Ordinal);
        HashSet<string> allowedFlags = new(flagOptions, StringComparer.Ordinal);
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        flags = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < args.Length; index++)
        {
            string token = args[index];
            if (allowedFlags.Contains(token))
            {
                if (!flags.Add(token))
                {
                    error = "Duplicate option: " + token + ".";
                    return false;
                }

                continue;
            }

            if (!allowedValues.Contains(token))
            {
                error = "Unknown option: " + token + ".";
                return false;
            }

            if (values.ContainsKey(token))
            {
                error = "Duplicate option: " + token + ".";
                return false;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = "Missing value for option: " + token + ".";
                return false;
            }

            values.Add(token, args[++index]);
        }

        error = null;
        return true;
    }

    private static bool TryGetRequired(
        Dictionary<string, string> values,
        string option,
        out string value)
    {
        if (values.TryGetValue(option, out string? candidate) && !string.IsNullOrWhiteSpace(candidate))
        {
            value = candidate;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static int UnknownCommand(string command, TextWriter standardError)
    {
        standardError.WriteLine("Unknown command: " + command + ". Run --help for usage.");
        return CliExitCodes.UsageError;
    }

    private static int WriteCommandHelp(string command, TextWriter output, TextWriter error)
    {
        switch (command)
        {
            case "inspect":
                output.WriteLine("Usage: repo2c4 inspect --repository PATH --output snapshot.json");
                output.WriteLine("Collects bounded local evidence only. It does not infer a C4 model.");
                return CliExitCodes.Success;
            case "generate":
                output.WriteLine("Usage: repo2c4 generate --model architecture.json --output DIR [--overwrite]");
                output.WriteLine("Generates specification.c4, model.c4 and views.c4 from a reviewed v1 model.");
                return CliExitCodes.Success;
            case "validate":
                output.WriteLine("Usage: repo2c4 validate --output DIR");
                output.WriteLine("Runs the installed official LikeC4 CLI against the generated directory.");
                return CliExitCodes.Success;
            default:
                error.WriteLine("Unknown command: " + command + ".");
                return CliExitCodes.UsageError;
        }
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("Repo2C4 CLI");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  inspect  --repository PATH --output snapshot.json");
        output.WriteLine("  generate --model architecture.json --output DIR [--overwrite]");
        output.WriteLine("  validate --output DIR");
        output.WriteLine();
        output.WriteLine("inspect records evidence only; generate requires a user-proposed/reviewed ArchitectureModel.");
        output.WriteLine("No command calls AI or converts package/project candidates into confirmed runtime architecture.");
    }

    private static void WriteContractErrors(ContractValidationException exception, TextWriter error)
    {
        foreach (ContractError contractError in exception.Errors)
        {
            error.WriteLine(contractError.Code + " at " + contractError.Path + ": " + contractError.Message);
        }
    }

    private static string CreateRepositoryId(string fullRepositoryPath)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(fullRepositoryPath);
        string name = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "repository";
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
        return "repo_" + Convert.ToHexString(digest).ToLowerInvariant()[..24];
    }

    private static bool IsReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static string NormalizeText(string content)
    {
        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.EndsWith('\n') ? normalized : normalized + "\n";
    }
}
