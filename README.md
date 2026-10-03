# Repo2C4

[Português (Brasil)](README.pt-BR.md)

Repo2C4 is a .NET 10 tool that turns bounded, traceable evidence from an authorized .NET repository into **reviewable C4 architecture documentation in LikeC4**. It is evidence-first: repository observations remain observations, architectural interpretation remains explicit, and unsupported hypotheses are never promoted to confirmed facts automatically.

## What is Repo2C4?

Repo2C4 addresses a common documentation problem: architecture diagrams drift because the repository, the architectural interpretation, and the generated documentation evolve independently.

The product separates that workflow into explicit stages:

```text
authorized repository
    -> bounded inspection
    -> versioned evidence snapshot with provenance
    -> human/client architectural review
    -> versioned ArchitectureModel
    -> deterministic LikeC4 preview
    -> explicit apply
    -> official LikeC4 validation and visualization
```

The evidence layer records what Repo2C4 can observe safely from supported .NET declarations. The model layer records architectural decisions and hypotheses. **Confirmed assertions must reference evidence IDs that exist in the embedded snapshot.** Assertions marked `requiresReview` may reference supporting evidence when available, but they may also intentionally have no evidence IDs when they represent an unsupported hypothesis; in that case the contract requires a `reviewReason`. A human reviewer—or an MCP client acting under human review—decides what the evidence means.

Repo2C4 keeps AI orchestration outside Core and MCP. Core performs no AI calls, and the MCP server contains no AI provider SDK or model selector. The optional `repo2c4-agent` is a separate Microsoft Agent Framework client of `repo2c4-mcp`: it can orchestrate evidence-first analysis, bounded validation/correction and human-approved writes without moving model intelligence into the MCP server. The CLI also retains its simpler optional inference adapters. AI output is never architectural truth by itself.

## Current capabilities and limitations

### Available today

- **Bounded local inspection:** scans one explicitly authorized local root, skips mandatory sensitive/generated paths and linked path escapes, enforces file/byte/entry budgets, and does not execute repository code.
- **Traceable .NET evidence:** extracts supported solution/project/source declarations with repository-relative provenance while keeping static declarations and runtime candidates distinct.
- **Deterministic LikeC4:** generates reviewable C1/C2 workspaces and validates them with the separately installed official LikeC4 CLI.
- **Selective multi-container C3:** expands one or more explicitly selected C2 containers when enough evidence is available; repeated selections share one deterministic workspace and unselected containers are not expanded automatically.
- **Review-first writes:** `generate` previews by default. CLI writes require `--apply`; MCP writes require explicit `dryRun=false`, `write=true`, and an authorized relative destination. Managed-file hashes protect human edits.
- **CLI:** `repo2c4` exposes onboarding, inspection, optional inference, generation and validation.
- **MCP stdio server:** `repo2c4-mcp` exposes evidence, generation and validation tools inside one authorized root. Model selection, if any, belongs to the MCP client.
- **Architecture Agent:** `repo2c4-agent` uses Microsoft Agent Framework as an independent MCP client, with explicit provider/model selection, bounded workflow/retries, sanitized observability and Agent Framework HITL before protected writes.
- **Optional public Git acquisition:** CLI can inspect an explicitly selected public HTTPS Git ref in a bounded temporary workspace. MCP exposes remote acquisition only when the host opts in with `--allow-remote-acquisition`.
- **Reviewable automation:** the repository includes a manually triggered workflow that can propose validated LikeC4 changes through a Pull Request without auto-merging them.

### Deliberate limitations

Repo2C4 currently does **not**:

- prove runtime communication, deployment topology, ownership or container boundaries merely from `ProjectReference`, package presence, SDKs or source candidates;
- evaluate MSBuild, build or execute the inspected repository, run its hooks/scripts, or read arbitrary source bodies into the public evidence contract;
- turn every .NET project into a C4 container or generate C3 for every container automatically;
- provide its own diagram renderer/editor—the generated workspace is rendered by LikeC4;
- support private/authenticated remote Git acquisition, SSH/file URLs, submodules, or analysis that depends on Git LFS;
- write generated architecture on inspection or inference alone;
- let the MCP server call a hosted AI provider on its own;
- remove the need for architectural review. `validate` verifies LikeC4 syntax/workspace integrity, not whether an architectural decision is true.

For the precise security and evidence boundaries, see [contracts](docs/contracts.md), [external integration interoperability](docs/external-integration-boundary.md), [CLI](docs/cli.md), [MCP](docs/mcp.md), and [distribution/security guidance](docs/distribution.md).

## Quick Start

The walkthrough below uses the checked-in `library-only` fixture so a new user can reproduce the same evidence and LikeC4 output. The fixture contains no executable host; that absence is intentional and demonstrates that Repo2C4 does not invent a runtime container.

### 1. Clone and install

```bash
git clone https://github.com/rodri-oliveira-dev/Repo2C4.git
cd Repo2C4

dotnet tool install --global Repo2C4.Cli
dotnet tool install --global Repo2C4.Mcp
dotnet tool install --global Repo2C4.Agent
npm install --global likec4@1.59.4
```

For your own repository, `repo2c4 init --repository /absolute/path` creates only local Repo2C4 configuration and `repo2c4 doctor --repository /absolute/path` checks prerequisites. Neither command inspects, builds, infers, or writes C4.

### 2. Inspect evidence

```bash
mkdir -p artifacts/quickstart

repo2c4 inspect \
  --repository examples/fixtures/library-only \
  --output artifacts/quickstart/snapshot.v1.json
```

The resulting snapshot is deterministic for this fixture and can be compared with [`examples/end-to-end/snapshot.v1.json`](examples/end-to-end/snapshot.v1.json).

For external integrations, first run `dotnet repo-inspect /absolute/repository --discover-integrations --output /absolute/repository/artifacts/inspection.json` with DotNetRepoInspector `v1.6.5`, then add `--integration-report /absolute/repository/artifacts/inspection.json` to `repo2c4 inspect`. Repo2C4 imports normalized `external.*` evidence through the public schema `1.6+` JSON boundary; it neither invokes DotNetRepoInspector nor reads its internal assemblies. See the [external integration boundary](docs/external-integration-boundary.md).

### 3. Review the model and preview LikeC4

Inspection stops at evidence. The checked-in [C1 model](examples/end-to-end/architecture.c1.v1.json) represents the next step—a deliberately reviewed architectural proposal bound to that snapshot.

```bash
repo2c4 generate \
  --model examples/end-to-end/architecture.c1.v1.json \
  --output artifacts/quickstart/c1
```

This is a **preview only**. No managed LikeC4 file is written yet.

### 4. Apply explicitly, validate, and visualize

After reviewing the preview, apply the managed output explicitly:

```bash
repo2c4 generate \
  --model examples/end-to-end/architecture.c1.v1.json \
  --output artifacts/quickstart/c1 \
  --apply

repo2c4 validate --output artifacts/quickstart/c1
```

To visualize the validated workspace with the separately installed LikeC4 CLI:

```bash
cd artifacts/quickstart/c1
likec4 start
```

LikeC4 serves the generated views locally. Repo2C4 itself does not expose a renderer.

### 5. Connect through MCP

Start the local stdio server with the smallest authorized absolute root:

```bash
repo2c4-mcp --repository-root "/absolute/path/to/Repo2C4/examples/fixtures/library-only"
```

An MCP client should call `inspect_repository` first, retrieve evidence as needed, propose/review a C1 or C2 model, call `generate_likec4` in its default dry-run mode, validate, and request explicit approval before any write. Copyable configurations for VS Code, Claude Desktop and portable stdio are in the [MCP quickstart](docs/mcp-quickstart.md).

The CLI fixture flow above is exercised in [CI](.github/workflows/ci.yml), including deterministic snapshot comparison, C1/C2 generation, selective C3, explicit apply, managed-conflict protection and official LikeC4 validation. The distribution smoke installs all three .NET tools, exercises the MCP protocol over stdio, and runs the packaged Agent against the local fixture through a controlled loopback Ollama-compatible fake.

### 6. Run the Agent (optional)

With Ollama running locally and an explicitly selected model:

```bash
repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --goal "Use only Repo2C4 MCP evidence. Produce conservative C1/C2 documentation."
```

The Agent is a client of MCP, not a replacement for it. Add `--write-destination docs/architecture` only when you want a validated proposal to reach a local human approval prompt before any write. See the [Agent guide](docs/agent.md).

## Example output: evidence → review → LikeC4

The reproducible input is [`examples/fixtures/library-only/OnlyLib.csproj`](examples/fixtures/library-only/OnlyLib.csproj):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
```

Inspection records facts with provenance rather than inventing architecture. The checked-in snapshot includes, among others:

```json
{
  "id": "ev_1d5d5849e8163d2cd54b7d77",
  "category": "dotnet.project.kind",
  "relativePath": "OnlyLib.csproj",
  "line": 1,
  "description": "Project declaration indicates kind: Library."
}
```

Human review then creates a model assertion tied to those evidence IDs. Because the repository contains no executable/host signal, the software-system boundary remains explicitly review-required:

```json
{
  "id": "el_library_repo",
  "kind": "softwareSystem",
  "name": "Library-only repository",
  "evidenceIds": [
    "ev_1d5d5849e8163d2cd54b7d77",
    "ev_cd4e2d70e36c097535a8395e"
  ],
  "status": "requiresReview"
}
```

Deterministic generation preserves that status in LikeC4 instead of silently confirming it:

```likec4
model {
  el_library_repo = softwareSystem "Library-only repository" {
    #requires-review
    metadata {
      architectureId "el_library_repo"
      reviewStatus "requiresReview"
    }
  }
}
```

The complete snapshot, reviewed C1/C2 models, expected generated files, evidence report and selective-C3 example live under [`examples/end-to-end/`](examples/end-to-end/README.md).

## Architecture and development

Repo2C4 keeps delivery hosts separate from the evidence/generation core:

| Project | Responsibility |
| --- | --- |
| `src/Repo2C4.Core` | Versioned evidence/model contracts, bounded inspection, evidence-backed .NET fact extraction, C1/C2 + selective-C3 policy, deterministic LikeC4 emission, managed-output planning and validation adapters. |
| `src/Repo2C4.Cli` | User-facing CLI for onboarding, local/public-remote inspection, optional inference, preview/apply generation and validation. |
| `src/Repo2C4.Mcp` | Local stdio MCP host with an explicit repository-root boundary; evidence and LikeC4 tools by default, optional public remote acquisition only when enabled by the host. |
| `src/Repo2C4.Agent` | Independent Microsoft Agent Framework client of Repo2C4 MCP; explicit model/provider, evidence-first orchestration, bounded workflow, observability and HITL write approval. |
| `tests/Repo2C4.*.Tests` | Contract, security-boundary, deterministic-generation, CLI, Agent and real MCP protocol coverage. |

CLI and MCP reference Core, never each other. Agent references neither Core nor the CLI/MCP projects as domain libraries; it connects to the MCP executable over stdio. Core does not reference the hosts. For maintainers, the detailed contracts, budgets, scanner behavior, security constraints, tests and distribution mechanics follow below.

### Prerequisites and verification

Install the .NET 10 SDK selected in `global.json` and Git. From the repository root:

```bash
dotnet tool restore
dotnet restore Repo2C4.slnx --locked-mode
dotnet format Repo2C4.slnx --verify-no-changes --no-restore
dotnet build Repo2C4.slnx --configuration Release --no-restore
dotnet test Repo2C4.slnx --configuration Release --no-build
dotnet test Repo2C4.slnx --configuration Release --no-build --coverlet --coverlet-output-format cobertura
```

The baseline retains Central Package Management, committed SDK-generated package locks, analyzers, nullable checks, deterministic builds, warnings as errors and NuGet auditing.

### Bounded local inventory

`RepositoryScanner.Scan(new RepositoryScanOptions(absoluteRoot, "stable_repo_id"), cancellationToken)` inspects **one explicitly authorized local directory**. The root must exist, be absolute without `..` and not be a symlink/junction. Child symlinks, junctions and reparse points are skipped rather than followed. Snapshot paths are normalized and relative to the authorized root. This scanner performs no writes, build, code execution, shell calls, network requests or external transmission. The offline CLI exposes this capability through `inspect`; MCP exposes the same Core inspection path through bounded `inspect_repository`, `get_evidence` and `get_snapshot` tools.

Default exclusions include `.git`, `bin`, `obj`, `node_modules`, `artifacts` and other generated directories, `.env*`, `appsettings.*`, credentials, keys and names indicative of secrets. Only known text-oriented extensions are considered. A bounded 4 KiB prefix probe rejects files with NUL bytes. Caller-supplied `IncludePatterns` and `ExcludePatterns` accept bounded relative globs (`*`, `?`, `**`), without overriding mandatory safety exclusions.

Default budgets are **1,000 accepted files**, **1 MiB per file**, **16 MiB total accepted file sizes** and **20,000 visited filesystem entries**. Adjust `MaxFiles`, `MaxBytesPerFile`, `MaxTotalBytes` and `MaxVisitedEntries` explicitly to fit the authorized checkout. The returned `RepositorySnapshot` includes only file-relative paths, sizes and bounded diagnostics, never source bodies or raw secrets. `sha256` is null because whole-file hashes are not computed. `scan.*` diagnostics give observed omission counts for excluded, inaccessible, binary and oversized files or limit exhaustion. When the entry budget stops enumeration, `scan.entryLimit` warns that unvisited entries were not counted and omission totals are **lower bounds**. Cancellation throws `OperationCanceledException`, never returning a partial result as a successful snapshot. Two scans of an unchanged repository yield the same `ContractJson.SerializeSnapshot` output.

**Security boundary:** the caller must authorize the root and run against a trusted, stable, preferably read-only checkout with least-privilege permissions. Managed pre/post-open link checks cannot guarantee atomic no-follow semantics during concurrent, malicious filesystem changes. Future readers of snapshot paths must revalidate containment, permissions, links and byte limits at the actual point of use. A nominally safe text file can still contain secrets. The optional OpenAI CLI provider requires explicit `--allow-external-ai` consent and sends only a bounded sanitized metadata projection. Offline inspection and MCP never contact an AI provider.

### Evidence-backed .NET facts

`RepositoryFactExtractor.Extract(options, cancellationToken)` inventories an explicitly authorized local checkout with `RepositoryScanner`, then inspects only accepted .NET solution, project and source files to populate `RepositorySnapshot.Evidence`. It recognizes .sln/.slnx project listings, declared executable/library/test characteristics, target frameworks, build-time `ProjectReference`, HTTP/worker host signals, Npgsql/RabbitMQ/Redis integration **candidates**, and Docker/compose manifest presence. Facts retain relative file paths and lines where available. A build-time project dependency is not a runtime relationship; an SDK, package or source signal is not proof of a deployed C4 container or a live network connection.

Extraction reads at most **512 KiB per accepted file**, rechecks the authorized root and symlinks, rejects invalid UTF-8/XML DTDs and external entities, and reports changed, inaccessible or unresolvable files via bounded `extract.*` diagnostics. Source text, connection strings, exception text and other potential secrets never enter the output. Managed path checks are not atomic against malicious concurrent filesystem mutations; use a trusted, stable read-only checkout. No external services are contacted by inspection. The CLI exposes this path through `inspect`; MCP exposes the same v1 evidence through bounded, paginated evidence tools.

See [fixtures, v1 snapshot and reproduction](examples/README.md) and [evidence categories](docs/contracts.md).

### Deterministic LikeC4 emission

`LikeC4Emitter.Emit(model)` converts a validated `ArchitectureModel` into `specification.c4`, `model.c4` and `views.c4` entirely in memory. Generation is deterministic, emits LF line endings, preserves C1/C2 containment and keeps `requiresReview` visible in LikeC4 tags/metadata rather than promoting hypotheses to confirmed architecture.

LikeC4 local identifiers are derived from v1 architecture IDs by replacing `.` with `_`; collisions in the same LikeC4 scope are rejected instead of receiving arbitrary suffixes. Names, relation descriptions and review reasons are quoted/escaped so DSL-looking text cannot introduce new statements. The emitter performs no repository I/O, process execution, AI/network calls or PR operations.

See [deterministic LikeC4 generation](docs/likec4-generation.md) and the checked-in golden files under `examples/likec4-golden/`.

### Official LikeC4 validation

`LikeC4CliValidator.ValidateAsync(workspace)` invokes only the official `likec4 validate` command in the selected workspace. It returns a structured result for success, validation failure, timeout, missing workspace or unavailable CLI, without returning raw LikeC4 stdout/stderr or source lines.

Runtime code does **not** install Node.js or LikeC4. The CI integration baseline explicitly pins Node.js `22.23.3` and `likec4@1.59.4`, then validates the generated C1/C2 golden workspaces plus controlled syntax/reference failures. Ordinary Core tests still require only .NET and no network.

See [LikeC4 CLI validation](docs/likec4-validation.md) for installation, command, exit codes, diagnostic safety and integration-test boundaries.

### Offline CLI workflow

The CLI provides the complete local flow:

```bash
dotnet src/Repo2C4.Cli/bin/Release/net10.0/Repo2C4.Cli.dll inspect \
  --repository examples/fixtures/library-only \
  --output artifacts/snapshot.v1.json

dotnet src/Repo2C4.Cli/bin/Release/net10.0/Repo2C4.Cli.dll generate \
  --model examples/end-to-end/architecture.c2.v1.json \
  --output artifacts/likec4

dotnet src/Repo2C4.Cli/bin/Release/net10.0/Repo2C4.Cli.dll validate \
  --output artifacts/likec4
```

`inspect` produces evidence only. A human-proposed/reviewed `ArchitectureModel` remains an explicit boundary before `generate`. Generation is preview-only by default. `--apply` writes only Repo2C4-managed files whose current SHA-256 still matches `.repo2c4-manifest.json`; manual edits and unmanaged collisions become conflicts and remain untouched.

Optional direct CLI inference proposes a review-required model. Choose local Ollama with `infer --snapshot snapshot.json --provider ollama --model-id IDENTIFIER --output candidate.json`, or choose OpenAI using `--provider openai --allow-external-ai` with `OPENAI_API_KEY` in the host environment. The offline CLI and MCP do not depend on either provider.

| Inference mode | Provider/selection | Network, cost and confidentiality |
| --- | --- | --- |
| MCP client | The external client selects its own model; the Repo2C4 MCP host has no provider connection. | Evidence is exposed to the authorized client; the client's configuration determines any further external sharing or charges. |
| Ollama local CLI | Explicit `--provider ollama` and installed local model ID. | Uses loopback HTTP without a cloud API key; local compute cost, no direct cloud request by Repo2C4. |
| OpenAI cloud CLI | Explicit `--provider openai --allow-external-ai`, model ID and host-provided `OPENAI_API_KEY`. | Sends only a bounded sanitized evidence projection to the fixed cloud API, which can incur token-based charges. Sanitized architectural metadata still leaves the machine. |

See the [local inference guide (EN)](docs/inference.md), [local guide (PT-BR)](docs/inference.pt-BR.md), [cloud consent and privacy guide (EN)](docs/inference-openai.md) and [cloud guide (PT-BR)](docs/inference-openai.pt-BR.md).

Usage is documented in [English](docs/cli.md) and [Português](docs/cli.pt-BR.md). The [end-to-end example](examples/end-to-end/README.md) includes the deterministic snapshot, reviewed C1/C2 models and expected generated LikeC4 files.

### MCP stdio foundation

The MCP host runs locally over stdio using the maintained MCP .NET SDK. Starting the server requires one explicitly authorized absolute repository root:

```bash
dotnet src/Repo2C4.Mcp/bin/Release/net10.0/Repo2C4.Mcp.dll \
  --repository-root /absolute/path/to/repository
```

`REPO2C4_REPOSITORY_ROOT` is the local configuration fallback when the command-line option is not supplied. The root must already exist and must not be a symbolic link, junction or reparse point. Tool paths are constrained to this root; absolute paths, parent traversal and linked path components are rejected. `stdout` is exclusively MCP protocol traffic, while help and diagnostics use `stderr`.

The read-only `inspect_repository`, `get_evidence` and `get_snapshot` tools expose bounded evidence. `generate_likec4` and `validate_likec4` provide protected generation and validation: generation is dry-run by default, writing requires explicit dual authorization plus a relative destination, and managed regeneration uses a manifest/hash diff so manual edits are not overwritten. Validation reuses the controlled official LikeC4 CLI adapter.

The server embeds no AI provider and does not select models. The supplied `ArchitectureModel` must match the session snapshot exactly; fabricated evidence is rejected. Repository-static/candidate evidence cannot be promoted by the MCP server into a confirmed container boundary or runtime relation; architectural interpretation stays with the client.

A deterministic metadata-only `evidence-report.md` is generated beside LikeC4 output and is also available through the read-only `get_evidence_report` MCP tool. See [evidence report and review workflow](docs/evidence-report.md).

See [MCP stdio, inspection/LikeC4 tools and access policy](docs/mcp.md) for tool semantics and [MCP client workflow](docs/mcp-client.md) for generic client configuration, the reusable evidence-first prompt and deterministic C1/C2 reproduction.

### Entry point smoke tests

```bash
dotnet run --project src/Repo2C4.Cli/Repo2C4.Cli.csproj -- --help
dotnet run --project src/Repo2C4.Mcp/Repo2C4.Mcp.csproj -- --help
dotnet run --project src/Repo2C4.Agent/Repo2C4.Agent.csproj -- --help
```

CLI help is written to stdout. MCP help and diagnostics are written **only to stderr**. The MCP test suite starts the executable through a vendor-neutral JSON-RPC stdio client, performs real handshakes/tool calls, exercises pagination/security failures, reproduces both versioned C1 and C2 models, compares preview/written `.c4` files with goldens, and verifies that no non-protocol content is written to stdout.

### CI and distribution

`.github/workflows/ci.yml` validates locked restore, formatting, Release build, tests, coverage, pinned LikeC4 integration, the complete offline CLI cycle (`inspect -> reviewed model -> generate -> validate`) and the full MCP protocol-client C1/C2 flow without paid AI or a proprietary client. CodeQL, Dependency Review and optional SonarQube Cloud checks remain available; [Sonar setup](docs/sonarqube-cloud.md) requires `SONAR_TOKEN`.

**Distribution:** the three versioned .NET tools, `Repo2C4.Cli`, `Repo2C4.Mcp` and `Repo2C4.Agent`, are clean-install tested. Public release remains manually gated; an authorized release publishes the exact validated payload to NuGet.org and GitHub Packages in parallel, then creates the GitHub Release only after both package registries succeed. NuGet indexing is asynchronous and is not used as a post-publication release gate. `Repo2C4.Mcp` also includes versioned Official MCP Registry metadata in [`server.json`](server.json); registry publication is a separate manual workflow run only after the matching NuGet package is publicly consumable. `Repo2C4.Agent` remains a local .NET Tool/MCP client rather than a marketplace-specific agent package. See the [installation, security and release guide](docs/distribution.md) or [Português](docs/distribution.pt-BR.md).



### Selective multi-container C3

C1/C2 generation remains the default. The CLI accepts repeated `--c3-container <container-id>`; MCP clients should use the bounded `c3Containers` collection (with legacy `c3ContainerId` preserved for one container); and the Agent accepts repeated `--c3-container` values as an authorization set from which it must choose only evidence-supported targets. Generated component boundaries are evidence-linked, deterministic and reviewable. Unselected or unsupported containers are never expanded automatically.


### Managed regeneration

Managed regeneration is review-first. CLI `generate` previews file-level changes by default and `--apply` is required to persist them. MCP `generate_likec4` returns the same structured change summary when a destination is supplied. Repo2C4 records only its generated outputs in `.repo2c4-manifest.json`, never deletes unknown files, and blocks apply when a managed file was edited or removed outside Repo2C4.


### Manually triggered review-only LikeC4 PR

The [Reviewable LikeC4 documentation PR](.github/workflows/architecture-pr.yml) workflow is manually dispatched **only on the trusted default `main`**. It accepts this repository's authorized inspection root and either an existing, human-reviewed v1 model matching a fresh snapshot or explicitly consented, sanitized OpenAI cloud inference. It performs locked restore, format/build/test, guarded managed generation, official LikeC4 validation and a bounded diff. A dedicated short-lived write-permission job proposes a PR **only if validated managed files changed**; it never pushes to `main`, approves or merges a PR, or turns AI hypotheses into confirmed architecture. For input examples, security/permission configuration, confidentiality, caveats and fixture tests, see [workflow guide](docs/architecture-pr.md). Its script and test fixtures are validated by CI before the workflow can be used from the default branch.

### Optional remote Git inspection

Local paths remain the primary and most private source. For a public HTTPS Git repository, Repo2C4 can acquire a selected ref in an isolated temporary workspace and feed that workspace into the same bounded evidence scanner:

```bash
repo2c4 inspect --remote-url https://github.com/OWNER/REPOSITORY.git --remote-ref refs/heads/main --output snapshot.json
```

The resolved URL, requested ref and concrete commit are written separately to `snapshot.json.acquisition.json`; they are acquisition provenance, not architectural evidence. Remote acquisition requires network access and Git, rejects embedded credentials, SSH/file URLs, submodules and symbolic links, applies file/size/time limits, never runs repository builds, hooks or scripts, and deletes the temporary workspace after inspection. Private/authenticated repositories and Git LFS-dependent analysis are intentionally unsupported. Review the confidentiality and trust implications before acquiring third-party code.
