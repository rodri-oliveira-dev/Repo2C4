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

    public static bool TryParse(
        string[] args,
        out AgentHostOptions? options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? provider = null;
        string? model = null;
        string? prompt = null;
        string? endpoint = null;
        string? repositoryRoot = null;
        string? mcpServerPath = null;
        int timeoutSeconds = 90;
        bool timeoutSpecified = false;
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

                case "--prompt":
                    if (!TryReadUniqueValue(args, ref index, prompt, "--prompt", out string? promptValue, out error))
                    {
                        options = null;
                        return false;
                    }

                    prompt = promptValue;
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

        options = new AgentHostOptions(provider, model, prompt?.Trim())
        {
            Endpoint = configuredEndpoint,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            AllowExternalAi = allowExternalAi,
            RepositoryRoot = repositoryRoot?.Trim(),
            McpServerPath = mcpServerPath?.Trim(),
        };
        error = null;
        return true;
    }

    private static bool IsValidModel(string model) =>
        model.Length is >= 1 and <= 128
        && model.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-' or ':' or '/');

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
