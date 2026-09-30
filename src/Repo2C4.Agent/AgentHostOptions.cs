namespace Repo2C4.Agent;

/// <summary>Explicit configuration for the Repo2C4 Agent host.</summary>
public sealed record AgentHostOptions(string Provider, string Model, string? Prompt)
{
    public static bool TryParse(
        string[] args,
        out AgentHostOptions? options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? provider = null;
        string? model = null;
        string? prompt = null;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--provider":
                    if (!TryReadValue(args, ref index, out string? providerValue))
                    {
                        return Fail("--provider requires a non-empty value.", out options, out error);
                    }

                    if (provider is not null)
                    {
                        return Fail("--provider may be specified only once.", out options, out error);
                    }

                    provider = providerValue;
                    break;

                case "--model":
                    if (!TryReadValue(args, ref index, out string? modelValue))
                    {
                        return Fail("--model requires a non-empty value.", out options, out error);
                    }

                    if (model is not null)
                    {
                        return Fail("--model may be specified only once.", out options, out error);
                    }

                    model = modelValue;
                    break;

                case "--prompt":
                    if (!TryReadValue(args, ref index, out string? promptValue))
                    {
                        return Fail("--prompt requires a non-empty value.", out options, out error);
                    }

                    if (prompt is not null)
                    {
                        return Fail("--prompt may be specified only once.", out options, out error);
                    }

                    prompt = promptValue;
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

        options = new AgentHostOptions(provider.Trim(), model.Trim(), prompt?.Trim());
        error = null;
        return true;
    }

    private static bool TryReadValue(string[] args, ref int index, out string? value)
    {
        if (index + 1 >= args.Length ||
            args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[index + 1]))
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
