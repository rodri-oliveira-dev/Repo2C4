using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Repo2C4.Cli;

internal sealed record Repo2C4LocalConfiguration(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("repositoryRoot")] string RepositoryRoot,
    [property: JsonPropertyName("outputDirectory")] string OutputDirectory,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("provider")] string? Provider);

internal static class OnboardingCommands
{
    internal const string ConfigurationFileName = ".repo2c4.json";
    private const string SchemaVersion = "repo2c4.config/v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> InitAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryParse(args, ["--repository", "--output-directory", "--mode", "--provider"], ["--non-interactive", "--force"], out var values, out var flags, out string? parseError))
        {
            error.WriteLine(parseError);
            return CliExitCodes.UsageError;
        }

        try
        {
            string root = Path.GetFullPath(values.GetValueOrDefault("--repository") ?? Environment.CurrentDirectory);
            if (!Directory.Exists(root) || IsReparsePoint(root))
            {
                error.WriteLine("Repository root must be an existing absolute non-link directory.");
                return CliExitCodes.IoError;
            }

            string configPath = Path.Combine(root, ConfigurationFileName);
            if (File.Exists(configPath) && !flags.Contains("--force"))
            {
                output.WriteLine("existing " + configPath);
                output.WriteLine("Configuration already exists; nothing changed.");
                return CliExitCodes.Success;
            }

            bool interactive = !flags.Contains("--non-interactive");
            string outputDirectory = values.GetValueOrDefault("--output-directory") ?? "docs/architecture";
            string mode = values.GetValueOrDefault("--mode") ?? "offline";
            string? provider = values.GetValueOrDefault("--provider");

            if (interactive)
            {
                outputDirectory = await PromptAsync(input, output, "Output directory", outputDirectory, cancellationToken).ConfigureAwait(false);
                mode = await PromptAsync(input, output, "Mode (offline|mcp|inference)", mode, cancellationToken).ConfigureAwait(false);
                if (string.Equals(mode, "inference", StringComparison.Ordinal))
                {
                    provider = await PromptAsync(input, output, "Provider (ollama|openai)", provider ?? "ollama", cancellationToken).ConfigureAwait(false);
                }

                output.Write("Create " + ConfigurationFileName + "? [y/N] ");
                string? confirmation = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(confirmation, "y", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(confirmation, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine("Cancelled; nothing changed.");
                    return CliExitCodes.Success;
                }
            }

            if (!TryValidate(root, outputDirectory, mode, provider, out string? validationError))
            {
                error.WriteLine(validationError);
                return CliExitCodes.UsageError;
            }

            Repo2C4LocalConfiguration configuration = new(SchemaVersion, root, outputDirectory, mode, provider);
            string json = JsonSerializer.Serialize(configuration, JsonOptions) + "\n";
            RejectSensitiveConfiguration(json);

            if (File.Exists(configPath))
            {
                string backup = configPath + ".bak";
                if (File.Exists(backup))
                {
                    error.WriteLine("Backup already exists; refusing to overwrite configuration.");
                    return CliExitCodes.IoError;
                }

                File.Copy(configPath, backup);
                output.WriteLine("backup " + backup);
            }

            await File.WriteAllTextAsync(configPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            output.WriteLine("created " + configPath);
            output.WriteLine("Next:");
            output.WriteLine("  repo2c4 doctor --repository \"" + root + "\"");
            output.WriteLine("  repo2c4 inspect --repository \"" + root + "\" --output snapshot.json");
            output.WriteLine("  repo2c4 generate --model architecture.reviewed.json --output \"" + Path.Combine(root, outputDirectory) + "\"");
            output.WriteLine("  repo2c4 validate --output \"" + Path.Combine(root, outputDirectory) + "\"");
            output.WriteLine("No repository analysis, inference, build, or C4 write was executed.");
            return CliExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            error.WriteLine("Initialization failed because the repository/configuration path could not be accessed.");
            return CliExitCodes.IoError;
        }
    }

    public static async Task<int> DoctorAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryParse(args, ["--repository"], [], out var values, out _, out string? parseError))
        {
            error.WriteLine(parseError);
            return CliExitCodes.UsageError;
        }

        try
        {
            string root = Path.GetFullPath(values.GetValueOrDefault("--repository") ?? Environment.CurrentDirectory);
            List<(string Name, bool Ok, string Detail)> checks = [];
            checks.Add(("repository", Directory.Exists(root) && !IsReparsePoint(root), "existing absolute non-link root"));

            Repo2C4LocalConfiguration? config = null;
            string configPath = Path.Combine(root, ConfigurationFileName);
            if (File.Exists(configPath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
                    RejectSensitiveConfiguration(json);
                    config = JsonSerializer.Deserialize<Repo2C4LocalConfiguration>(json);
                    bool valid = config is not null
                        && string.Equals(config.SchemaVersion, SchemaVersion, StringComparison.Ordinal)
                        && TryValidate(root, config.OutputDirectory, config.Mode, config.Provider, out _)
                        && string.Equals(Path.GetFullPath(config.RepositoryRoot), root, PathComparison);
                    checks.Add(("configuration", valid, valid ? "valid " + SchemaVersion : "invalid or root mismatch"));
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or NotSupportedException)
                {
                    checks.Add(("configuration", false, "invalid or contains sensitive fields"));
                }
            }
            else
            {
                checks.Add(("configuration", false, ConfigurationFileName + " not found; run repo2c4 init"));
            }

            checks.Add(("filesystem", CanProbe(root), "repository root read/write probe"));
            checks.Add(("dotnet", await CommandSucceedsAsync("dotnet", "--version", cancellationToken).ConfigureAwait(false), ".NET executable"));
            checks.Add(("likec4", await CommandSucceedsAsync("likec4", "--version", cancellationToken).ConfigureAwait(false), "LikeC4 executable"));

            if (config is not null && string.Equals(config.Mode, "mcp", StringComparison.Ordinal))
            {
                checks.Add(("mcp", await CommandSucceedsAsync("repo2c4-mcp", "--help", cancellationToken).ConfigureAwait(false), "repo2c4-mcp executable"));
            }

            if (config is not null && string.Equals(config.Mode, "inference", StringComparison.Ordinal))
            {
                bool providerOk = config.Provider switch
                {
                    "ollama" => await CommandSucceedsAsync("ollama", "--version", cancellationToken).ConfigureAwait(false),
                    "openai" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
                    _ => false,
                };
                checks.Add(("provider", providerOk, config.Provider == "openai" ? "OPENAI_API_KEY present (value not read or printed)" : "selected provider executable"));
            }

            foreach (var check in checks)
            {
                output.WriteLine((check.Ok ? "PASS" : "FAIL") + " " + check.Name + ": " + check.Detail);
            }

            bool success = checks.All(static check => check.Ok);
            if (!success) error.WriteLine("Doctor found one or more onboarding problems.");
            return success ? CliExitCodes.Success : CliExitCodes.ValidationFailed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            error.WriteLine("Doctor could not access the selected repository.");
            return CliExitCodes.IoError;
        }
    }

    private static async Task<string> PromptAsync(TextReader input, TextWriter output, string label, string defaultValue, CancellationToken cancellationToken)
    {
        output.Write(label + " [" + defaultValue + "]: ");
        string? value = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static bool TryValidate(string root, string outputDirectory, string mode, string? provider, out string? error)
    {
        if (Path.IsPathRooted(outputDirectory) || outputDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..", StringComparer.Ordinal))
        {
            error = "Output directory must be a relative path inside the repository.";
            return false;
        }

        if (mode is not ("offline" or "mcp" or "inference"))
        {
            error = "Mode must be offline, mcp, or inference.";
            return false;
        }

        if (mode == "inference" && provider is not ("ollama" or "openai"))
        {
            error = "Inference mode requires provider ollama or openai.";
            return false;
        }

        if (mode != "inference" && provider is not null)
        {
            error = "Provider is only valid for inference mode.";
            return false;
        }

        string destination = Path.GetFullPath(Path.Combine(root, outputDirectory));
        if (!destination.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison))
        {
            error = "Output directory must remain inside the repository.";
            return false;
        }

        error = null;
        return true;
    }

    private static void RejectSensitiveConfiguration(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            string name = property.Name.ToLowerInvariant();
            if (name.Contains("token", StringComparison.Ordinal) || name.Contains("secret", StringComparison.Ordinal)
                || name.Contains("key", StringComparison.Ordinal) || name.Contains("password", StringComparison.Ordinal)
                || name.Contains("content", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Sensitive configuration fields are not allowed.");
            }
        }
    }

    private static bool CanProbe(string root)
    {
        try
        {
            string path = Path.Combine(root, ".repo2c4-doctor-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    private static async Task<bool> CommandSucceedsAsync(string command, string argument, CancellationToken cancellationToken)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo(command, argument) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true },
            };
            if (!process.Start()) return false;
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
    }

    private static bool TryParse(string[] args, string[] valueOptions, string[] flagOptions, out Dictionary<string,string> values, out HashSet<string> flags, out string? error)
    {
        values = new(StringComparer.Ordinal); flags = new(StringComparer.Ordinal);
        HashSet<string> allowedValues = new(valueOptions, StringComparer.Ordinal); HashSet<string> allowedFlags = new(flagOptions, StringComparer.Ordinal);
        for (int i=0;i<args.Length;i++)
        {
            string token=args[i];
            if (allowedFlags.Contains(token)) { if(!flags.Add(token)){error="Duplicate option: "+token+".";return false;} continue; }
            if(!allowedValues.Contains(token)){error="Unknown option: "+token+".";return false;}
            if(values.ContainsKey(token)||i+1>=args.Length||args[i+1].StartsWith("--",StringComparison.Ordinal)){error="Missing or duplicate value for option: "+token+".";return false;}
            values[token]=args[++i];
        }
        error=null; return true;
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
