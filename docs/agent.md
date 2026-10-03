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

## Quick start with Ollama

For the shortest local path, have .NET 10, Node.js/npm, LikeC4, Ollama and one local model available. Confirm Ollama can see the model before running the Agent:

```bash
ollama list
# If needed:
ollama pull YOUR_LOCAL_MODEL
```

After the Repo2C4 packages are publicly released, install the Agent and its required MCP server:

```bash
dotnet tool install --global Repo2C4.Agent
dotnet tool install --global Repo2C4.Mcp
npm install --global likec4@1.59.4

repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "/absolute/path/to/repository" \
  --goal "Document the current C1 and C2 architecture conservatively."
```

If DotNetRepoInspector `v1.6.5` has already produced an `InspectionReport` schema `1.6+` inside the repository, authorize that relative path explicitly:

```bash
repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "/absolute/path/to/repository" \
  --integration-report "artifacts/inspection.json" \
  --goal "Document C1 and C2 including external integrations."
```

The Agent does not open the report. Its host policy passes only this authorized path to `inspect_repository`, then the Agent reads the resulting session snapshot through `get_evidence`. It preserves `external.*` direction and confidence, and does not promote low-confidence or ambiguous evidence to confirmed architecture.

This first command is **analysis-only**: without `--write-destination`, the Agent does not create managed `.c4` files. It prints the workflow status, run ID, counters, diagnostics and the model-produced summary to the terminal.

A typical successful analysis ends with fields similar to:

```text
Status: requires_review
Validation attempts: 1
Run ID: <run-id>
Terminal reason: requires_review
Tool calls: <count>
Workflow iterations: <count>
Evidence pages: <count>
Response characters: <count>
Context characters: <count>
```

`completed` means the proposal validated without pending `requiresReview` assertions. `requires_review` means LikeC4 validation succeeded but architectural assertions still need human review.

## Install

Repo2C4 Agent, MCP and CLI use the same product version. Agent requires MCP; installing CLI is optional for the Agent flow and is shown here only for the broader Repo2C4 workflow.

The package commands below describe the **published NuGet path** and assume the packages are available in the configured feed:

```bash
dotnet tool install --global Repo2C4.Agent
dotnet tool install --global Repo2C4.Mcp
dotnet tool install --global Repo2C4.Cli
repo2c4-agent --help
```

Before a public release, run from a trusted source checkout instead:

```bash
dotnet build Repo2C4.slnx

dotnet src/Repo2C4.Agent/bin/Debug/net10.0/Repo2C4.Agent.dll \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --mcp-server-path "$(pwd)/src/Repo2C4.Mcp/bin/Debug/net10.0/Repo2C4.Mcp.dll" \
  --goal "Document the current C1 and C2 architecture conservatively."
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

The provider and model are always explicit. Repo2C4 does not silently choose or download a model. If `ollama list` does not show the selected model, pull or configure it in Ollama before invoking Repo2C4.

Without `--write-destination`, this mode never persists managed architecture files. It is safe to use as the default first run when evaluating a repository.

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

### PowerShell examples

Use PowerShell environment syntax and resolve the repository root to an absolute path:

```powershell
$repo = (Resolve-Path ".\examples\fixtures\library-only").Path

repo2c4-agent `
  --provider ollama `
  --model YOUR_LOCAL_MODEL `
  --repository-root $repo `
  --goal "Document the current C1 and C2 architecture conservatively."
```

For OpenAI:

```powershell
$env:OPENAI_API_KEY = "from-your-secret-store"

repo2c4-agent `
  --provider openai `
  --model YOUR_OPENAI_MODEL `
  --allow-external-ai `
  --repository-root $repo `
  --goal "Document the current C1 and C2 architecture conservatively."
```

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

## Selective multi-container C3 with the Agent

C1/C2 remain the default. Repeat `--c3-container` to authorize a bounded set of possible C3 targets for the run:

```bash
repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "/absolute/path/to/repository" \
  --c3-container "container_api" \
  --c3-container "container_worker" \
  --goal "Document C1/C2 and propose C3 only for relevant containers supported by evidence."
```

Up to 8 container IDs may be authorized. These values are an allow-list, not an instruction to expand every listed container. The Agent must explicitly request only the subset relevant to the objective and supported by the reviewed C2 evidence. The host intersects that request with the allow-list, so repository content or model output cannot escalate C3 to another target. Ambiguous or unsupported component boundaries remain `requiresReview` or are omitted.

A single `--c3-container` preserves the prior behavior. For multi-container proposals, the exact selected IDs are carried through dry-run preview, validation, correction attempts and the destination-specific HITL write plan. The approval prompt shows the selected C3 containers before any protected write.

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

For `--write-destination "docs/architecture"`, C1 and C2 are prepared under `docs/architecture/c1` and `docs/architecture/c2` respectively. The destination is repository-relative and must pass the existing MCP output protections.

After successful validation, the host prepares an immutable destination-specific preview and Microsoft Agent Framework emits an `ApprovalRequiredAIFunction` request. The console shows destination, files/changes and `requiresReview` IDs. Only an explicit local approval can invoke the protected write. Repository text, model output and prompts cannot approve it.

The MCP write gateway rechecks preview freshness and managed-output conflicts immediately before applying. A stale preview or human-edited managed file aborts the write rather than overwriting it. C1 and C2 destinations are separate MCP writes rather than a filesystem transaction. The gateway preflights the complete approved plan before starting; if a later concurrent failure or cancellation occurs after one destination was applied, the terminal write result explicitly lists the destinations already written and requires a fresh preview/validation cycle before retrying.

## Operational limits

Safe defaults are configurable only inside bounded ranges:

| Limit | Option | Default | Allowed |
| --- | --- | ---: | ---: |
| Total run duration | `--max-duration-seconds` | 300 s | 1–1800 |
| Provider request timeout | `--timeout-seconds` | 90 s | 1–300 |
| Tool calls | `--max-tool-calls` | 40 | 1–100 |
| Workflow iterations | `--max-workflow-iterations` | 3 | 1–3 |
| Validation attempts | `--max-validation-attempts` | 2 | 1–3 |
| Authorized C3 containers | repeated `--c3-container` | 0 | 0–8 |
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
