# Optional local Ollama inference (Phase 5, issue #20)

The CLI supports an explicit, opt-in `infer` command. `inspect`, `generate`, `validate`, and the MCP server remain offline and work without Ollama. No cloud account, API key or hosted model is needed for inference tests. This command never writes LikeC4 files, edits source files or validates its own architectural conclusions.

## Run a local example

Install and start [Ollama](https://ollama.com/) on the same machine. Pull a locally available JSON-capable model, then use its exact installed model ID (the example ID below is illustrative, not a required download):

```bash
ollama pull YOUR_MODEL_ID
ollama serve

dotnet build Repo2C4.slnx --configuration Release
CLI="dotnet src/Repo2C4.Cli/bin/Release/net10.0/Repo2C4.Cli.dll"

$CLI infer \
  --snapshot examples/end-to-end/snapshot.v1.json \
  --provider ollama \
  --model-id YOUR_MODEL_ID \
  --output artifacts/inference/candidate.json
```

Use `--endpoint http://127.0.0.1:11434/` to select a different **local** Ollama port, and `--timeout-seconds 90` to override the default (valid range 1–300 seconds). Only HTTP loopback origins are supported: `localhost`, `127.0.0.1` and `[::1]`. Credentials, embedded URL paths, remote hosts and HTTPS/cloud endpoints are rejected. The runtime HTTP client bypasses system proxies for this local connection.

`candidate.json` must not exist beforehand. Input snapshots must be valid v1 JSON and no larger than 4 MiB. The sanitized projection is bounded to 256 inventoried files and 512 pieces of evidence; the HTTP request and response are limited to 96 KiB and 256 KiB respectively. Over-limit, malformed, unavailable and timed-out responses are rejected, and the candidate file is not created. No provider response bodies, original evidence descriptions or tokens are logged.

The inference adapter only transmits opaque file aliases, evidence IDs, fixed-category summaries and a stable repository ID. It **does not transmit raw repository file bodies, original filenames/paths, original free-form descriptions, scan diagnostics, hashes or secrets**. This privacy boundary deliberately reduces the model's ability to infer detailed architecture. It is a conservative proposal, not a repository analysis service. No local source code is executed by this command.

## Review before generation

Open `candidate.json` and compare each element/relation to the original snapshot and, when needed, the repository itself. Every inference-generated assertion has `status: "requiresReview"`. The CLI enforces this even if the provider tries to mark an assertion as confirmed. A reference to an evidence ID establishes provenance, not actual runtime behavior. Revisit system boundaries, container candidates, actors and relationships; remove unsupported assertions or keep them explicitly under review. Never accept suggestions as architectural facts without independently checking their meaning.

The output embeds the **original locally supplied snapshot** for provenance, even though Ollama received only its sanitized projection. Keep `candidate.json` private if the source metadata may be sensitive. Model names are written to the CLI diagnostic stream for audit, but no authorization token is used by the local provider.

After reviewing and saving a separate `architecture.reviewed.json`, preview, apply and validate the generated LikeC4 files:

```bash
$CLI generate --model architecture.reviewed.json --output artifacts/inference/likec4
$CLI generate --model architecture.reviewed.json --output artifacts/inference/likec4 --apply
$CLI validate --output artifacts/inference/likec4
```

`validate` requires an installed official LikeC4 CLI, described in [LikeC4 validation](likec4-validation.md). Generated content remains protected by the existing manifest and preview policy.

## Deterministic automated example

`InferenceTests` uses an injected fake `HttpMessageHandler`, not an installed Ollama process. It checks a valid v1 candidate, original-snapshot preservation, mandatory human review, removal of secret-shaped metadata from HTTP requests, invalid JSON and contracts, local model unavailability, response limits, timeout, caller cancellation, non-loopback endpoints and overwrite prevention. This keeps the test suite fully reproducible without network access or credentials.
