# Repo2C4 Agent

Repo2C4 Agent is the local architecture-documentation agent shipped as the `Repo2C4.Agent` .NET Tool, command `repo2c4-agent`. It uses Microsoft Agent Framework for model/tool orchestration and acts strictly as a **client of the independently usable `Repo2C4.Mcp` stdio server**. The Agent does not reference Repo2C4 Core, CLI or MCP as architecture-domain libraries.

The intended flow is:

```text
explicit goal
  -> Agent Framework
  -> Repo2C4 MCP inspect/evidence tools
  -> evidence-first C1/C2 proposal
  -> host-controlled evidence report + dry-run LikeC4 preview
  -> bounded LikeC4 validation/correction workflow
  -> optional Agent Framework HITL approval
  -> MCP protected write
  -> validated LikeC4 files
```

Repository content, evidence descriptions, diagnostics and tool results are untrusted data. They never authorize writes and never override the Agent's host policy.

## Install

Repo2C4 Agent, MCP and CLI use the same product version. Agent requires MCP; installing CLI is optional for the Agent flow and is shown here only for the broader Repo2C4 workflow:

```bash
dotnet tool install --global Repo2C4.Agent --version 1.0.0
dotnet tool install --global Repo2C4.Mcp --version 1.0.0
dotnet tool install --global Repo2C4.Cli --version 1.0.0
repo2c4-agent --help
```

The Agent requires .NET 10. The MCP server must be available either as the installed `repo2c4-mcp` command or through an explicit absolute `--mcp-server-path`. LikeC4 remains an independent dependency used by validation; CI pins `likec4@1.59.4`.

## Ollama: local provider

Ollama is the local provider. Repo2C4 only accepts an HTTP loopback origin for `--endpoint`; the default is `http://127.0.0.1:11434/`.

```bash
repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "/absolute/path/to/repository" \
  --goal "Document the current C1 and C2 architecture conservatively."
```

The provider and model are always explicit. Repo2C4 does not silently choose or download a model.

## OpenAI: explicit external consent

OpenAI is hosted/external. Use it only when repository metadata is allowed to leave the host:

```bash
export OPENAI_API_KEY="provided-by-your-secret-store"

repo2c4-agent \
  --provider openai \
  --model YOUR_OPENAI_MODEL \
  --allow-external-ai \
  --repository-root "/absolute/path/to/repository" \
  --goal "Document the current C1 and C2 architecture conservatively."
```

`--allow-external-ai` is mandatory. The API key is read only from the Agent process environment, is never accepted as a CLI argument, and is not inherited by the MCP child process. Structured logs do not include full prompts, raw evidence, tool arguments or secrets.

## MCP connection and independence

When `--mcp-server-path` is omitted, the Agent launches the installed `repo2c4-mcp` command and passes the authorized repository root. An explicit server path may point to an installed executable or to an absolute `Repo2C4.Mcp.dll`.

```bash
repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "/absolute/path/to/repository" \
  --mcp-server-path "/absolute/path/to/repo2c4-mcp" \
  --goal "Explain the current architecture and its unresolved boundaries."
```

The MCP server remains a standalone product. VS Code, Claude Desktop or another MCP host may use `repo2c4-mcp` directly without installing or running `repo2c4-agent`. The Agent adds one opinionated orchestration client; it does not move model intelligence into MCP.

## Analysis, validation and bounded correction

For each run the Agent:

1. starts with `inspect_repository`;
2. retrieves bounded snapshot/evidence pages needed for the proposal;
3. submits C1 and C2 through MCP `generate_likec4` in forced preview-only mode;
4. lets the host call `get_evidence_report`;
5. lets the host call `validate_likec4`;
6. if validation fails, sends only sanitized diagnostics into a bounded correction turn;
7. returns a structured terminal result.

Public terminal statuses are `completed`, `requires_review`, `validation_failed`, `cancelled` and `failed`. Insufficient evidence is reported as `failed` with terminal reason `insufficient_evidence`; budget failures use explicit terminal reason codes.

A validated LikeC4 workspace does **not** prove that the architecture claims are true. Evidence quality and review status remain visible.

## Human approval and writing

Without `--write-destination`, the Agent never asks to write.

To make a validated proposal eligible for local writing:

```bash
repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "/absolute/path/to/repository" \
  --goal "Document C1 and C2 and prepare an approved local update." \
  --write-destination "docs/architecture"
```

After successful validation, the host prepares an immutable destination-specific preview and Microsoft Agent Framework emits an `ApprovalRequiredAIFunction` request. The console shows destination, files/changes and `requiresReview` IDs. Only an explicit local approval can invoke the protected write. Repository text, model output and prompts cannot approve it.

The MCP write gateway rechecks preview freshness and managed-output conflicts immediately before applying. A stale preview or human-edited managed file aborts the write rather than overwriting it.

## Operational limits

Safe defaults are configurable only inside bounded ranges:

| Limit | Option | Default | Allowed |
| --- | --- | ---: | ---: |
| Total run duration | `--max-duration-seconds` | 300 s | 1–1800 |
| Provider request timeout | `--timeout-seconds` | 90 s | 1–300 |
| Tool calls | `--max-tool-calls` | 40 | 1–100 |
| Workflow iterations | `--max-workflow-iterations` | 3 | 1–3 |
| Validation attempts | `--max-validation-attempts` | 3 | 1–3 |
| Evidence pages | `--max-evidence-pages` | 20 | 1–50 |
| Accumulated response | `--max-response-chars` | 32000 | 1024–100000 |
| Accumulated context | `--max-context-chars` | 64000 | 4096–200000 |

Cancellation propagates from the host through Agent Framework, the workflow and MCP. Every run has a run ID, counters and sanitized structured logs. OpenTelemetry instrumentation is optional; no collector/exporter is required for normal operation.

## Reproducible fixture walkthrough

From a trusted Repo2C4 checkout, install the three tools and LikeC4, then run the Agent against the checked-in library-only fixture:

```bash
npm install --global likec4@1.59.4

repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --goal "Use only Repo2C4 MCP evidence. Produce conservative C1/C2 documentation; keep unsupported boundaries requiresReview."
```

Expected behavior for this fixture:

- MCP proves a .NET library project and target framework;
- the lack of an executable/runtime host prevents the Agent from confirming a deployment/container boundary;
- useful hypotheses remain `requiresReview`;
- generation remains preview-only unless `--write-destination` is supplied;
- with a write destination, a human approval prompt appears only **after** validation.

A copyable walkthrough is also available in [examples/agent/README.md](../examples/agent/README.md).

## Troubleshooting

**`provide --repository-root as an existing absolute local directory`**  
Use an existing absolute directory. The Agent deliberately refuses an implicit current directory.

**`the local Repo2C4 MCP server could not be started or initialized`**  
Install `Repo2C4.Mcp`, ensure `repo2c4-mcp` is on `PATH`, or pass an absolute `--mcp-server-path`.

**Ollama request failed/timed out**  
Confirm Ollama is listening on loopback, the selected model exists, and the timeout is adequate. Non-loopback Ollama endpoints are rejected.

**OpenAI configuration fails before analysis**  
Provide `OPENAI_API_KEY` through the process environment and add `--allow-external-ai`. Do not place the key in command history, files or arguments.

**`validation_failed`**  
Run LikeC4 locally if deeper DSL diagnostics are needed. The Agent exposes only bounded sanitized diagnostics to the correction turn.

**`insufficient_evidence`**  
This is a controlled outcome, not permission to invent architecture. Add stronger repository evidence or perform human architectural review.

**write conflict/stale preview**  
Preserve the human edit, resolve the destination state, rerun analysis/preview/validation, and approve the new immutable plan.

## Testing and provider smoke

Mandatory CI uses a scripted `IChatClient` plus the real MCP stdio process and does not require cloud credentials. See [agent deterministic testing](agent-testing.md). The optional real-provider workflow is manual-only and never runs as a normal push/PR gate.
