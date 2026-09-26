# Optional OpenAI cloud inference (Phase 5, issue #21)

Repo2C4 offers three distinct workflows. **MCP** sends repository evidence to the user's chosen MCP client, which decides whether and where an AI model is invoked; the Repo2C4 MCP host itself has no model, provider credential or AI network call. **Ollama** is the opt-in local CLI inference provider: it calls an HTTP loopback server without cloud API credentials. **OpenAI** is the opt-in cloud CLI provider: it sends a bounded, sanitized evidence projection to `https://api.openai.com/v1/responses` **only** after `--allow-external-ai` is supplied and `OPENAI_API_KEY` is present in the process environment.

Cloud inference can incur charges determined by your API account, chosen model, input/output tokens and current provider pricing. Before opting in, check [OpenAI API pricing](https://openai.com/api/pricing/) and your organization's data-handling rules. Even sanitized metadata leaves your machine; it may expose architecture clues such as technology categories, relative grouping through opaque file aliases, evidence counts and repeated references. Sanitization is not a guarantee of anonymity. Review the [OpenAI API data usage policy](https://platform.openai.com/docs/guides/your-data) before processing confidential repositories. The request sets `store: false` to disable optional Responses storage, but does not override the provider's other processing, abuse-monitoring or retention policies.

## Explicit opt-in example

Make the API credential available through a secure host secret or environment variable. Do not put it in shell history, code, snapshot files, arguments, versioned configuration or CI logs. The model ID is supplied by the caller and is never selected from a baked-in list. Choose a model supported by your API project that can return JSON-mode Responses; not every model or account supports the same API features.

```bash
CLI="dotnet src/Repo2C4.Cli/bin/Release/net10.0/Repo2C4.Cli.dll"

$CLI infer \
  --snapshot examples/end-to-end/snapshot.v1.json \
  --provider openai \
  --model-id YOUR_SUPPORTED_MODEL_ID \
  --allow-external-ai \
  --output artifacts/inference/cloud-candidate.json
```

Without `--allow-external-ai`, the CLI rejects the command **before reading credentials or sending any HTTP request**. Without `OPENAI_API_KEY`, it rejects the cloud request before transmission. `--endpoint` applies only to local Ollama; there is no arbitrary cloud destination override. Use optional `--timeout-seconds 90` to configure a 1–300-second deadline. The cloud adapter does not follow HTTP redirects, use cookies or forward HTTP requests through ambient proxies. Only the fixed HTTPS Responses API destination is allowed.

Immediately before the call, stderr reports the destination and counts of **anonymized file entries** and **sanitized evidence records** from the selected snapshot, plus the chosen provider/model metadata. It never logs the API key, original file paths, free-form descriptions, prompt, raw response or provider error body. The provider sends only the shared sanitized projection: opaque repository and evidence IDs, anonymous file aliases, fixed-vocabulary factual categories and source types. It does not send raw repository files, diagnostics, original paths, hashes or secret-bearing descriptions. The response is limited to 256 KiB and the request to 96 KiB; malformed, truncated, refused, failed or contract-invalid proposals are rejected and no candidate is created. HTTP 401/403/429/5xx produce bounded diagnostic messages without provider response bodies; the caller's cancellation is propagated.

The candidate file embeds the **original local snapshot**, not the sanitized projection. Treat it as potentially sensitive. All generated elements and relations are forced to `requiresReview` even if the model calls them confirmed. Inspect and edit the candidate before using the existing `generate` preview, `--apply` and `validate` commands. No inference provider independently proves runtime relationships or makes automatic source-code changes.

The full, deterministic CI suite uses fake HTTP messages and a local fixture. It never requires a live API key, a cloud account or an external inference request. For the local alternative, see [Ollama inference](inference.md); for MCP-client behavior, see [MCP client workflow](mcp-client.md).
