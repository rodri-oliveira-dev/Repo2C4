using System.Security.Cryptography;
using System.Text;
using Repo2C4.Cli.Inference;
using Repo2C4.Core.C3;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.Generation;
using Repo2C4.Core.Inspection;
using Repo2C4.Core.LikeC4;
using Repo2C4.Core.Review;

namespace Repo2C4.Cli;

public static class CliExitCodes
{
    public const int Success = 0;
    public const int UsageError = 2;
    public const int InvalidData = 3;
    public const int ValidationFailed = 4;
    public const int IoError = 5;
    public const int InferenceFailed = 6;
}

internal static class CliApplication
{
    private const long MaxModelBytes = 4L * 1024 * 1024;

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken,
        HttpClient? inferenceClient = null)
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
            "infer" => await RunInferAsync(commandArguments, standardOutput, standardError, inferenceClient, cancellationToken)
                .ConfigureAwait(false),
            "inspect" => await RunInspectAsync(commandArguments, standardOutput, standardError, cancellationToken)
                .ConfigureAwait(false),
            "generate" => await RunGenerateAsync(commandArguments, standardOutput, standardError, cancellationToken)
                .ConfigureAwait(false),
            "validate" => await RunValidateAsync(commandArguments, standardOutput, standardError, cancellationToken)
                .ConfigureAwait(false),
            _ => UnknownCommand(command, standardError),
        };
    }

    private static async Task<int> RunInferAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        HttpClient? inferenceClient,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(
            args,
            ["--snapshot", "--provider", "--model-id", "--output", "--endpoint", "--timeout-seconds"],
            [],
            out Dictionary<string, string> values,
            out _,
            out string? parseError))
        {
            standardError.WriteLine(parseError);
            return CliExitCodes.UsageError;
        }

        if (!TryGetRequired(values, "--snapshot", out string snapshotPath)
            || !TryGetRequired(values, "--provider", out string providerName)
            || !TryGetRequired(values, "--model-id", out string modelId)
            || !TryGetRequired(values, "--output", out string outputPath))
        {
            standardError.WriteLine("infer requires --snapshot FILE --provider ollama --model-id IDENTIFIER --output FILE.");
            return CliExitCodes.UsageError;
        }

        if (!string.Equals(providerName, "ollama", StringComparison.Ordinal))
        {
            standardError.WriteLine("Only the local ollama provider is supported.");
            return CliExitCodes.UsageError;
        }

        int timeoutSeconds = 90;
        if (values.TryGetValue("--timeout-seconds", out string? timeoutText)
            && (!int.TryParse(timeoutText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out timeoutSeconds)
                || timeoutSeconds is < 1 or > 300))
        {
            standardError.WriteLine("--timeout-seconds must be between 1 and 300.");
            return CliExitCodes.UsageError;
        }

        try
        {
            string fullSnapshot = Path.GetFullPath(snapshotPath);
            string fullOutput = Path.GetFullPath(outputPath);
            if (!File.Exists(fullSnapshot))
            {
                standardError.WriteLine("Snapshot input does not exist.");
                return CliExitCodes.IoError;
            }

            if (new FileInfo(fullSnapshot).Length > MaxModelBytes)
            {
                standardError.WriteLine("Snapshot exceeds the 4 MiB CLI input limit.");
                return CliExitCodes.InvalidData;
            }

            if (File.Exists(fullOutput))
            {
                standardError.WriteLine("Candidate output already exists; choose a new path.");
                return CliExitCodes.IoError;
            }

            string json = await File.ReadAllTextAsync(fullSnapshot, cancellationToken).ConfigureAwait(false);
            RepositorySnapshot snapshot = ContractJson.DeserializeSnapshot(json);
            string endpoint = values.GetValueOrDefault("--endpoint") ?? "http://127.0.0.1:11434/";
            using HttpClientHandler? ownedHandler = inferenceClient is null
                ? new HttpClientHandler
                {
                    UseProxy = false,
                    UseCookies = false,
                    AllowAutoRedirect = false,
                    CheckCertificateRevocationList = true,
                }
                : null;
            using HttpClient? ownedClient = ownedHandler is null
                ? null
                : new HttpClient(ownedHandler, disposeHandler: false);
            if (ownedClient is not null)
            {
                ownedClient.Timeout = Timeout.InfiniteTimeSpan;
            }

            IArchitectureInferenceProvider provider = new OllamaInferenceProvider(
                inferenceClient ?? ownedClient!,
                endpoint,
                modelId,
                TimeSpan.FromSeconds(timeoutSeconds));
            ArchitectureModel candidate = await ArchitectureInference.ProposeAsync(
                snapshot,
                provider,
                cancellationToken).ConfigureAwait(false);

            string candidateJson = NormalizeText(ContractJson.SerializeModel(candidate));
            if (Encoding.UTF8.GetByteCount(candidateJson) > MaxModelBytes)
            {
                throw new InferenceException(InferenceFailure.PayloadTooLarge, "Candidate exceeds the 4 MiB CLI model limit.");
            }

            string? outputDirectory = Path.GetDirectoryName(fullOutput);
            if (outputDirectory is null)
            {
                standardError.WriteLine("Candidate output file path is invalid.");
                return CliExitCodes.UsageError;
            }

            Directory.CreateDirectory(outputDirectory);
            string temporary = fullOutput + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await WriteTextFileAsync(
                    temporary,
                    candidateJson,
                    overwrite: false,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, fullOutput);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            standardOutput.WriteLine(fullOutput);
            standardError.WriteLine("provider=ollama model=" + modelId + "; candidate requires human review.");
            return CliExitCodes.Success;
        }
        catch (InferenceException exception)
        {
            standardError.WriteLine(exception.Message);
            return exception.Failure is InferenceFailure.InvalidInput
                or InferenceFailure.InvalidResponse
                or InferenceFailure.PayloadTooLarge
                ? CliExitCodes.InvalidData
                : CliExitCodes.InferenceFailed;
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
        catch (ArgumentException)
        {
            standardError.WriteLine("Invalid inference option or local path.");
            return CliExitCodes.UsageError;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException)
        {
            standardError.WriteLine("Inference failed because a local path could not be read or written.");
            return CliExitCodes.IoError;
        }
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

            await WriteTextFileAsync(fullOutput, json, overwrite: false, cancellationToken).ConfigureAwait(false);
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
            ["--model", "--output", "--c3-container"],
            ["--apply"],
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
            IReadOnlyList<LikeC4GeneratedFile> files;
            if (values.TryGetValue("--c3-container", out string? selectedContainer))
            {
                ArchitectureC3Model c3 = ArchitectureC3Builder.Build(model, selectedContainer);
                files = LikeC4Emitter.EmitWithC3(model, c3);
            }
            else
            {
                files = LikeC4Emitter.Emit(model);
            }

            EvidenceReportResult report = EvidenceReportGenerator.Generate(model);
            List<LikeC4GeneratedFile> managedFiles =
            [
                .. files.Select(file => new LikeC4GeneratedFile(file.FileName, NormalizeText(file.Content))),
                new LikeC4GeneratedFile(report.FileName, NormalizeText(report.Content)),
            ];

            string outputRoot = Path.GetFullPath(outputPath);
            if (Directory.Exists(outputRoot) && IsReparsePoint(outputRoot))
            {
                standardError.WriteLine("Output directory must not be a symlink, junction or reparse point.");
                return CliExitCodes.IoError;
            }

            GenerationPlan plan = await ManagedOutputManager.PreviewAsync(
                outputRoot,
                model.SchemaVersion,
                managedFiles,
                cancellationToken).ConfigureAwait(false);

            foreach (GeneratedFileChange change in plan.Changes)
            {
                standardOutput.WriteLine(change.Kind.ToString().ToLowerInvariant() + " " + change.FileName);
            }

            if (plan.HasConflicts)
            {
                standardError.WriteLine("Generation preview found conflicts. Manually edited or unmanaged files were not changed.");
                return CliExitCodes.IoError;
            }

            if (!flags.Contains("--apply"))
            {
                standardOutput.WriteLine("preview-only");
                return CliExitCodes.Success;
            }

            await ManagedOutputManager.CommitAsync(
                outputRoot,
                model.SchemaVersion,
                managedFiles,
                plan,
                cancellationToken).ConfigureAwait(false);
            standardOutput.WriteLine("applied " + ManagedOutputManager.ManifestFileName);
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
            case "infer":
                output.WriteLine("Usage: repo2c4 infer --snapshot FILE --provider ollama --model-id IDENTIFIER --output candidate.json [--endpoint http://127.0.0.1:11434/] [--timeout-seconds 90]");
                output.WriteLine("Produces a review-required candidate from sanitized metadata using a local Ollama server; never generates C4 files.");
                return CliExitCodes.Success;
            case "inspect":
                output.WriteLine("Usage: repo2c4 inspect --repository PATH --output snapshot.json");
                output.WriteLine("Collects bounded local evidence only. It does not infer a C4 model.");
                return CliExitCodes.Success;
            case "generate":
                output.WriteLine("Usage: repo2c4 generate --model architecture.json --output DIR [--c3-container ID] [--apply]");
                output.WriteLine("Previews deterministic C1/C2 outputs and evidence-report.md; --apply writes only managed, unchanged outputs. --c3-container ID adds selected C3.");
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
        output.WriteLine("  infer    --snapshot snapshot.json --provider ollama --model-id IDENTIFIER --output candidate.json");
        output.WriteLine("  generate --model architecture.json --output DIR [--c3-container ID] [--apply]");
        output.WriteLine("  validate --output DIR");
        output.WriteLine();
        output.WriteLine("inspect records evidence only; generate requires a user-proposed/reviewed ArchitectureModel.");
        output.WriteLine("Only infer calls a configured local AI provider. All AI proposals require human review.");
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

    private static async Task WriteTextFileAsync(
        string path,
        string content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        FileMode mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        using FileStream stream = new(
            path,
            mode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        await stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeText(string content)
    {
        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.EndsWith('\n') ? normalized : normalized + "\n";
    }
}
