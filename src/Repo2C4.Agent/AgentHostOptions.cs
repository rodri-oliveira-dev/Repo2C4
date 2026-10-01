namespace Repo2C4.Agent;

/// <summary>Explicit configuration for the Repo2C4 Agent host.</summary>
public sealed record AgentHostOptions(string Provider, string Model, string? Prompt)
{
    public const string OllamaProvider = "ollama";
    public const string OpenAiProvider = "openai";
    public const string OpenAiApiKeyEnvironmentVariable = "OPENAI_API_KEY";

    public static readonly Uri DefaultOllamaEndpoint = new("http://127.0.0.1:11434/");

    public Uri? Endpoint
    {
        get;
        init;
    }

    public TimeSpan Timeout
    {
        get;
        init;
    } = TimeSpan.FromSeconds(90);

    public TimeSpan MaxRunDuration
    {
        get;
        init;
    } = TimeSpan.FromSeconds(300);

    public bool AllowExternalAi
    {
        get;
        init;
    }

    public string? RepositoryRoot
    {
        get;
        init;
    }

    public string? McpServerPath
    {
        get;
        init;
    }

    public string? Goal => Prompt;

    public string? C3ContainerId
    {
        get;
        init;
    }

    public int MaxValidationAttempts
    {
        get;
        init;
    } = 2;

    public int MaxToolCalls
    {
        get;
        init;
    } = 40;

    public int MaxWorkflowIterations
    {
        get;
        init;
    } = 3;

    public int MaxEvidencePages
    {
        get;
        init;
    } = 20;

    public int MaxResponseCharacters
    {
        get;
        init;
    } = 32_000;

    public int MaxContextCharacters
    {
        get;
        init;
    } = 64_000;

    public string? WriteDestination
    {
        get;
        init;
    }

    internal AgentExecutionContext? ExecutionContext
    {
        get;
        init;
    }

    public static bool TryParse(
        string[] args,
        out AgentHostOptions? options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? provider = null;
        string? model = null;
        string? prompt = null;
        string? goalOption = null;
        string? endpoint = null;
        string? repositoryRoot = null;
        string? mcpServerPath = null;
        string? c3ContainerId = null;
        string? writeDestination = null;
        int timeoutSeconds = 90;
        int maxDurationSeconds = 300;
        int maxValidationAttempts = 2;
        int maxToolCalls = 40;
        int maxWorkflowIterations = 3;
        int maxEvidencePages = 20;
        int maxResponseCharacters = 32_000;
        int maxContextCharacters = 64_000;
        bool timeoutSpecified = false;
        bool maxDurationSpecified = false;
        bool maxValidationAttemptsSpecified = false;
        bool maxToolCallsSpecified = false;
        bool maxWorkflowIterationsSpecified = false;
        bool maxEvidencePagesSpecified = false;
        bool maxResponseCharactersSpecified = false;
        bool maxContextCharactersSpecified = false;
        bool allowExternalAi = false;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--provider":
                    if (!TryReadUniqueValue(args, ref index, provider, "--provider", out string? providerValue, out error))
                    {
                        options = null;
                        return false;
                    }

                    provider = providerValue;
                    break;

                case "--model":
                    if (!TryReadUniqueValue(args, ref index, model, "--model", out string? modelValue, out error))
                    {
                        options = null;
                        return false;
                    }

                    model = modelValue;
                    break;

                case "--goal":
                case "--prompt":
                    if (prompt is not null)
                    {
                        return Fail(
                            "provide only one analysis objective using --goal (or the legacy --prompt alias).",
                            out options,
                            out error);
                    }

                    string currentGoalOption = args[index];
                    if (!TryReadUniqueValue(
                            args,
                            ref index,
                            prompt,
                            currentGoalOption,
                            out string? promptValue,
                            out error))
                    {
                        options = null;
                        return false;
                    }

                    prompt = promptValue;
                    goalOption = currentGoalOption;
                    break;

                case "--endpoint":
                    if (!TryReadUniqueValue(args, ref index, endpoint, "--endpoint", out string? endpointValue, out error))
                    {
                        options = null;
                        return false;
                    }

                    endpoint = endpointValue;
                    break;

                case "--repository-root":
                    if (!TryReadUniqueValue(
                            args,
                            ref index,
                            repositoryRoot,
                            "--repository-root",
                            out string? repositoryRootValue,
                            out error))
                    {
                        options = null;
                        return false;
                    }

                    repositoryRoot = repositoryRootValue;
                    break;

                case "--mcp-server-path":
                    if (!TryReadUniqueValue(
                            args,
                            ref index,
                            mcpServerPath,
                            "--mcp-server-path",
                            out string? mcpServerPathValue,
                            out error))
                    {
                        options = null;
                        return false;
                    }

                    mcpServerPath = mcpServerPathValue;
                    break;

                case "--c3-container":
                    if (!TryReadUniqueValue(
                            args,
                            ref index,
                            c3ContainerId,
                            "--c3-container",
                            out string? c3ContainerValue,
                            out error))
                    {
                        options = null;
                        return false;
                    }

                    c3ContainerId = c3ContainerValue;
                    break;

                case "--write-destination":
                    if (!TryReadUniqueValue(
                            args,
                            ref index,
                            writeDestination,
                            "--write-destination",
                            out string? writeDestinationValue,
                            out error))
                    {
                        options = null;
                        return false;
                    }

                    writeDestination = writeDestinationValue;
                    break;

                case "--max-validation-attempts":
                    if (maxValidationAttemptsSpecified)
                    {
                        return Fail("--max-validation-attempts may be specified only once.", out options, out error);
                    }

                    if (!TryReadValue(args, ref index, out string? attemptsValue)
                        || !int.TryParse(
                            attemptsValue,
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out maxValidationAttempts)
                        || maxValidationAttempts is < 1 or > 3)
                    {
                        return Fail("--max-validation-attempts must be between 1 and 3.", out options, out error);
                    }

                    maxValidationAttemptsSpecified = true;
                    break;

                case "--max-duration-seconds":
                    if (!TryReadBoundedInt(
                            args,
                            ref index,
                            "--max-duration-seconds",
                            ref maxDurationSpecified,
                            1,
                            1_800,
                            out maxDurationSeconds,
                            out error))
                    {
                        options = null;
                        return false;
                    }

                    break;

                case "--max-tool-calls":
                    if (!TryReadBoundedInt(
                            args,
                            ref index,
                            "--max-tool-calls",
                            ref maxToolCallsSpecified,
                            1,
                            100,
                            out maxToolCalls,
                            out error))
                    {
                        options = null;
                        return false;
                    }

                    break;

                case "--max-workflow-iterations":
                    if (!TryReadBoundedInt(
                            args,
                            ref index,
                            "--max-workflow-iterations",
                            ref maxWorkflowIterationsSpecified,
                            1,
                            3,
                            out maxWorkflowIterations,
                            out error))
                    {
                        options = null;
                        return false;
                    }

                    break;

                case "--max-evidence-pages":
                    if (!TryReadBoundedInt(
                            args,
                            ref index,
                            "--max-evidence-pages",
                            ref maxEvidencePagesSpecified,
                            1,
                            50,
                            out maxEvidencePages,
                            out error))
                    {
                        options = null;
                        return false;
                    }

                    break;

                case "--max-response-chars":
                    if (!TryReadBoundedInt(
                            args,
                            ref index,
                            "--max-response-chars",
                            ref maxResponseCharactersSpecified,
                            1_024,
                            100_000,
                            out maxResponseCharacters,
                            out error))
                    {
                        options = null;
                        return false;
                    }

                    break;

                case "--max-context-chars":
                    if (!TryReadBoundedInt(
                            args,
                            ref index,
                            "--max-context-chars",
                            ref maxContextCharactersSpecified,
                            4_096,
                            200_000,
                            out maxContextCharacters,
                            out error))
                    {
                        options = null;
                        return false;
                    }

                    break;

                case "--timeout-seconds":
                    if (timeoutSpecified)
                    {
                        return Fail("--timeout-seconds may be specified only once.", out options, out error);
                    }

                    if (!TryReadValue(args, ref index, out string? timeoutValue)
                        || !int.TryParse(
                            timeoutValue,
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out timeoutSeconds)
                        || timeoutSeconds is < 1 or > 300)
                    {
                        return Fail("--timeout-seconds must be between 1 and 300.", out options, out error);
                    }

                    timeoutSpecified = true;
                    break;

                case "--allow-external-ai":
                    if (allowExternalAi)
                    {
                        return Fail("--allow-external-ai may be specified only once.", out options, out error);
                    }

                    allowExternalAi = true;
                    break;

                default:
                    return Fail("unsupported argument.", out options, out error);
            }
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            return Fail("provide --provider explicitly.", out options, out error);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return Fail("provide --model explicitly.", out options, out error);
        }

        provider = provider.Trim();
        model = model.Trim();

        if (provider is not OllamaProvider and not OpenAiProvider)
        {
            return Fail("supported providers are ollama and openai.", out options, out error);
        }

        if (!IsValidModel(model))
        {
            return Fail(
                "--model must contain 1-128 ASCII letters, digits, '.', '_', '-', ':' or '/'.",
                out options,
                out error);
        }

        Uri? configuredEndpoint = null;
        if (provider == OllamaProvider)
        {
            if (allowExternalAi)
            {
                return Fail("--allow-external-ai applies only to openai.", out options, out error);
            }

            if (!TryCreateOllamaEndpoint(endpoint, out configuredEndpoint))
            {
                return Fail(
                    "Ollama --endpoint must be an HTTP loopback origin such as http://127.0.0.1:11434/.",
                    out options,
                    out error);
            }
        }
        else
        {
            if (!allowExternalAi)
            {
                return Fail(
                    "OpenAI requires explicit --allow-external-ai consent.",
                    out options,
                    out error);
            }

            if (endpoint is not null)
            {
                return Fail("--endpoint applies only to ollama.", out options, out error);
            }
        }

        string? configuredC3ContainerId = c3ContainerId?.Trim();
        if (configuredC3ContainerId is not null && !IsValidArchitectureId(configuredC3ContainerId))
        {
            return Fail(
                "--c3-container must be a valid lowercase architecture element ID.",
                out options,
                out error);
        }

        string? configuredWriteDestination = writeDestination?.Trim();
        if (configuredWriteDestination is not null
            && !TryNormalizeRelativeDestination(
                configuredWriteDestination,
                out configuredWriteDestination))
        {
            return Fail(
                "--write-destination must be a safe repository-relative directory.",
                out options,
                out error);
        }

        if (configuredWriteDestination is not null && string.IsNullOrWhiteSpace(prompt))
        {
            return Fail(
                "--write-destination requires --goal.",
                out options,
                out error);
        }

        if (maxValidationAttempts > maxWorkflowIterations)
        {
            return Fail(
                "--max-workflow-iterations must be greater than or equal to --max-validation-attempts.",
                out options,
                out error);
        }

        _ = goalOption;
        options = new AgentHostOptions(provider, model, prompt?.Trim())
        {
            Endpoint = configuredEndpoint,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            MaxRunDuration = TimeSpan.FromSeconds(maxDurationSeconds),
            AllowExternalAi = allowExternalAi,
            RepositoryRoot = repositoryRoot?.Trim(),
            McpServerPath = mcpServerPath?.Trim(),
            C3ContainerId = configuredC3ContainerId,
            MaxValidationAttempts = maxValidationAttempts,
            MaxToolCalls = maxToolCalls,
            MaxWorkflowIterations = maxWorkflowIterations,
            MaxEvidencePages = maxEvidencePages,
            MaxResponseCharacters = maxResponseCharacters,
            MaxContextCharacters = maxContextCharacters,
            WriteDestination = configuredWriteDestination,
        };
        error = null;
        return true;
    }

    private static bool IsValidModel(string model) =>
        model.Length is >= 1 and <= 128
        && model.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-' or ':' or '/');

    private static bool IsValidArchitectureId(string id) =>
        id.Length is >= 1 and <= 128
        && id[0] is >= 'a' and <= 'z'
        && id.All(character =>
            character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.' or '_' or '-');

    private static bool TryNormalizeRelativeDestination(
        string value,
        out string? normalized)
    {
        normalized = value.Replace('\\', '/').Trim().Trim('/');

        if (normalized.Length is < 1 or > 240
            || Path.IsPathFullyQualified(value)
            || normalized.Any(char.IsControl))
        {
            normalized = null;
            return false;
        }

        string[] segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0
            || segments.Any(segment =>
                segment is "." or ".."
                || segment.Contains(':', StringComparison.Ordinal)))
        {
            normalized = null;
            return false;
        }

        normalized = string.Join("/", segments);
        return true;
    }

    private static bool TryCreateOllamaEndpoint(string? value, out Uri? endpoint)
    {
        if (value is null)
        {
            endpoint = DefaultOllamaEndpoint;
            return true;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && uri.IsLoopback
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.AbsolutePath == "/")
        {
            endpoint = uri;
            return true;
        }

        endpoint = null;
        return false;
    }

    private static bool TryReadBoundedInt(
        string[] args,
        ref int index,
        string option,
        ref bool specified,
        int minimum,
        int maximum,
        out int value,
        out string? error)
    {
        value = default;

        if (specified)
        {
            error = "Repo2C4 Agent configuration error: " + option + " may be specified only once.";
            return false;
        }

        if (!TryReadValue(args, ref index, out string? raw)
            || !int.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out value)
            || value < minimum
            || value > maximum)
        {
            error =
                "Repo2C4 Agent configuration error: "
                + option
                + " must be between "
                + minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " and "
                + maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ".";
            return false;
        }

        specified = true;
        error = null;
        return true;
    }

    private static bool TryReadUniqueValue(
        string[] args,
        ref int index,
        string? currentValue,
        string option,
        out string? value,
        out string? error)
    {
        if (currentValue is not null)
        {
            value = null;
            error = "Repo2C4 Agent configuration error: " + option + " may be specified only once.";
            return false;
        }

        if (!TryReadValue(args, ref index, out value))
        {
            error = "Repo2C4 Agent configuration error: " + option + " requires a non-empty value.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadValue(string[] args, ref int index, out string? value)
    {
        if (index + 1 >= args.Length
            || args[index + 1].StartsWith("--", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            value = null;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool Fail(
        string reason,
        out AgentHostOptions? options,
        out string? error)
    {
        options = null;
        error = "Repo2C4 Agent configuration error: " + reason;
        return false;
    }
}
