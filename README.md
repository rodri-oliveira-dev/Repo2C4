# Repo2C4

Repo2C4 is an evolving .NET 10 tool for collecting verifiable architectural evidence from local .NET repositories and, in later phases, generating reviewable LikeC4 documentation. Inference must not convert unsupported hypotheses into confirmed facts.

## Architecture and current scope

| Project | Responsibility |
| --- | --- |
| `src/Repo2C4.Core` | Versioned evidence contracts, safe local inventory, evidence-backed .NET declaration extraction and deterministic in-memory LikeC4 emission. |
| `src/Repo2C4.Cli` | Offline `inspect`, `generate` and `validate` commands; no AI calls or automatic architecture inference. |
| `src/Repo2C4.Mcp` | Local MCP server over stdio with an explicit repository-root boundary; MCP tools are added incrementally during phase 3. |
| `tests/Repo2C4.*.Tests` | Separate boundary and startup tests for each product project. |

CLI and MCP reference Core, never each other. Core does not reference the hosts. Core contains local inventory and evidence extraction of static .NET declarations, without deriving proven runtime architecture. AI providers and rendering are not implemented. MCP transport is local stdio only, and no inspection or LikeC4 MCP tools are exposed yet; those remain scoped to issues #14 and #15.

## Prerequisites and verification

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

## Bounded local inventory (issue #7)

`RepositoryScanner.Scan(new RepositoryScanOptions(absoluteRoot, "stable_repo_id"), cancellationToken)` inspects **one explicitly authorized local directory**. The root must exist, be absolute without `..` and not be a symlink/junction. Child symlinks, junctions and reparse points are skipped rather than followed. Snapshot paths are normalized and relative to the authorized root. This scanner performs no writes, build, code execution, shell calls, network requests or external transmission. The offline CLI exposes this capability through `inspect`; MCP scanning remains intentionally unexposed until issue #14.

Default exclusions include `.git`, `bin`, `obj`, `node_modules`, `artifacts` and other generated directories, `.env*`, `appsettings.*`, credentials, keys and names indicative of secrets. Only known text-oriented extensions are considered. A bounded 4 KiB prefix probe rejects files with NUL bytes. Caller-supplied `IncludePatterns` and `ExcludePatterns` accept bounded relative globs (`*`, `?`, `**`), without overriding mandatory safety exclusions.

Default budgets are **1,000 accepted files**, **1 MiB per file**, **16 MiB total accepted file sizes** and **20,000 visited filesystem entries**. Adjust `MaxFiles`, `MaxBytesPerFile`, `MaxTotalBytes` and `MaxVisitedEntries` explicitly to fit the authorized checkout. The returned `RepositorySnapshot` includes only file-relative paths, sizes and bounded diagnostics, never source bodies or raw secrets. `sha256` is null because whole-file hashes are not computed. `scan.*` diagnostics give observed omission counts for excluded, inaccessible, binary and oversized files or limit exhaustion. When the entry budget stops enumeration, `scan.entryLimit` warns that unvisited entries were not counted and omission totals are **lower bounds**. Cancellation throws `OperationCanceledException`, never returning a partial result as a successful snapshot. Two scans of an unchanged repository yield the same `ContractJson.SerializeSnapshot` output.

**Security boundary:** the caller must authorize the root and run against a trusted, stable, preferably read-only checkout with least-privilege permissions. Managed pre/post-open link checks cannot guarantee atomic no-follow semantics during concurrent, malicious filesystem changes. Future readers of snapshot paths must revalidate containment, permissions, links and byte limits at the actual point of use. A nominally safe text file can still contain secrets. Before any future provider sends content or metadata off-machine, obtain explicit user consent and apply appropriate secret redaction. This phase sends nothing to an AI provider.

## Evidence-backed .NET facts (issue #8)

`RepositoryFactExtractor.Extract(options, cancellationToken)` inventories an explicitly authorized local checkout with `RepositoryScanner`, then inspects only accepted .NET solution, project and source files to populate `RepositorySnapshot.Evidence`. It recognizes .sln/.slnx project listings, declared executable/library/test characteristics, target frameworks, build-time `ProjectReference`, HTTP/worker host signals, Npgsql/RabbitMQ/Redis integration **candidates**, and Docker/compose manifest presence. Facts retain relative file paths and lines where available. A build-time project dependency is not a runtime relationship; an SDK, package or source signal is not proof of a deployed C4 container or a live network connection.

Extraction reads at most **512 KiB per accepted file**, rechecks the authorized root and symlinks, rejects invalid UTF-8/XML DTDs and external entities, and reports changed, inaccessible or unresolvable files via bounded `extract.*` diagnostics. Source text, connection strings, exception text and other potential secrets never enter the output. Managed path checks are not atomic against malicious concurrent filesystem mutations; use a trusted, stable read-only checkout. No external services are contacted by inspection. The CLI exposes this path through `inspect`; MCP evidence tools remain intentionally unexposed until issue #14.

See [fixtures, v1 snapshot and reproduction](examples/README.md) and [evidence categories](docs/contracts.md).

## Deterministic LikeC4 emission (issue #10)

`LikeC4Emitter.Emit(model)` converts a validated `ArchitectureModel` into `specification.c4`, `model.c4` and `views.c4` entirely in memory. Generation is deterministic, emits LF line endings, preserves C1/C2 containment and keeps `requiresReview` visible in LikeC4 tags/metadata rather than promoting hypotheses to confirmed architecture.

LikeC4 local identifiers are derived from v1 architecture IDs by replacing `.` with `_`; collisions in the same LikeC4 scope are rejected instead of receiving arbitrary suffixes. Names, relation descriptions and review reasons are quoted/escaped so DSL-looking text cannot introduce new statements. The emitter performs no repository I/O, process execution, AI/network calls or PR operations.

See [deterministic LikeC4 generation](docs/likec4-generation.md) and the checked-in golden files under `examples/likec4-golden/`.

## Official LikeC4 validation (issue #11)

`LikeC4CliValidator.ValidateAsync(workspace)` invokes only the official `likec4 validate` command in the selected workspace. It returns a structured result for success, validation failure, timeout, missing workspace or unavailable CLI, without returning raw LikeC4 stdout/stderr or source lines.

Runtime code does **not** install Node.js or LikeC4. The CI integration baseline explicitly pins Node.js `22.23.3` and `likec4@1.59.4`, then validates the generated C1/C2 golden workspaces plus controlled syntax/reference failures. Ordinary Core tests still require only .NET and no network.

See [LikeC4 CLI validation](docs/likec4-validation.md) for installation, command, exit codes, diagnostic safety and integration-test boundaries.

## Offline CLI workflow (issue #12)

The Phase 2 CLI now provides the complete local flow:

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

`inspect` produces evidence only. A human-proposed/reviewed `ArchitectureModel` remains an explicit boundary before `generate`. Existing LikeC4 files are not replaced unless `--overwrite` is supplied.

Usage is documented in [English](docs/cli.md) and [Português](docs/cli.pt-BR.md). The [end-to-end example](examples/end-to-end/README.md) includes the deterministic snapshot, reviewed C1/C2 models and expected generated LikeC4 files.

## MCP stdio foundation (issue #13)

The Phase 3 MCP host now runs locally over stdio using the maintained MCP .NET SDK. Starting the server requires one explicitly authorized absolute repository root:

```bash
dotnet src/Repo2C4.Mcp/bin/Release/net10.0/Repo2C4.Mcp.dll \
  --repository-root /absolute/path/to/repository
```

`REPO2C4_REPOSITORY_ROOT` is the local configuration fallback when the command-line option is not supplied. The root must already exist and must not be a symbolic link, junction or reparse point. Future tool paths are constrained to this root; absolute paths, parent traversal and linked path components are rejected. `stdout` is exclusively MCP protocol traffic, while help and diagnostics use `stderr`.

Issue #13 deliberately exposes no domain tools: `tools/list` returns an empty list until issue #14. The server embeds no AI provider and does not select models. Architectural interpretation remains a client responsibility.

See [MCP stdio and access policy](docs/mcp.md) for configuration, limits and the security boundary.

## Entry point smoke tests

```bash
dotnet run --project src/Repo2C4.Cli/Repo2C4.Cli.csproj -- --help
dotnet run --project src/Repo2C4.Mcp/Repo2C4.Mcp.csproj -- --help
```

CLI help is written to stdout. MCP help and diagnostics are written **only to stderr**. The MCP test suite additionally starts the executable, performs a real stdio initialization handshake and `tools/list`, and verifies that no non-protocol content is written to stdout.

## CI and distribution

`.github/workflows/ci.yml` validates locked restore, formatting, Release build, tests, coverage, pinned LikeC4 integration, the complete offline CLI cycle (`inspect -> reviewed model -> generate -> validate`) and the MCP stdout boundary. CodeQL, Dependency Review and optional SonarQube Cloud checks remain available; [Sonar setup](docs/sonarqube-cloud.md) requires `SONAR_TOKEN`.

**Publication is disabled through phase 4:** projects are non-packable, the template's release workflow is removed, and CI produces no NuGet package. Installation and release distribution are defined in phase 5.

See [roadmap #4](https://github.com/rodri-oliveira-dev/Repo2C4/issues/4). Phase 3 issues #13–#16 share `phase/03-mcp`; the single phase pull request is opened only after the last issue is implemented.
