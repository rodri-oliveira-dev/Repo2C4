# Repo2C4

Repo2C4 is an evolving .NET 10 tool for collecting verifiable architectural evidence from local .NET repositories and, in later phases, generating reviewable LikeC4 documentation. Inference must not convert unsupported hypotheses into confirmed facts.

## Architecture and current scope

| Project | Responsibility |
| --- | --- |
| `src/Repo2C4.Core` | Versioned evidence contracts, safe local inventory and evidence-backed .NET declaration extraction. |
| `src/Repo2C4.Cli` | Offline CLI host; feature commands are not yet implemented. |
| `src/Repo2C4.Mcp` | Stdio-safe MCP host scaffold; protocol transport arrives in phase 3. |
| `tests/Repo2C4.*.Tests` | Separate boundary and startup tests for each product project. |

CLI and MCP reference Core, never each other. Core does not reference the hosts. Core contains local inventory and evidence extraction of static .NET declarations, without deriving proven runtime architecture. AI providers, rendering and live MCP transport are not yet implemented.

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

`RepositoryScanner.Scan(new RepositoryScanOptions(absoluteRoot, "stable_repo_id"), cancellationToken)` inspects **one explicitly authorized local directory**. The root must exist, be absolute without `..` and not be a symlink/junction. Child symlinks, junctions and reparse points are skipped rather than followed. Snapshot paths are normalized and relative to the authorized root. This scanner performs no writes, build, code execution, shell calls, network requests or external transmission. CLI and MCP scanning commands remain unimplemented in this phase.

Default exclusions include `.git`, `bin`, `obj`, `node_modules`, `artifacts` and other generated directories, `.env*`, `appsettings.*`, credentials, keys and names indicative of secrets. Only known text-oriented extensions are considered. A bounded 4 KiB prefix probe rejects files with NUL bytes. Caller-supplied `IncludePatterns` and `ExcludePatterns` accept bounded relative globs (`*`, `?`, `**`), without overriding mandatory safety exclusions.

Default budgets are **1,000 accepted files**, **1 MiB per file**, **16 MiB total accepted file sizes** and **20,000 visited filesystem entries**. Adjust `MaxFiles`, `MaxBytesPerFile`, `MaxTotalBytes` and `MaxVisitedEntries` explicitly to fit the authorized checkout. The returned `RepositorySnapshot` includes only file-relative paths, sizes and bounded diagnostics, never source bodies or raw secrets. `sha256` is null because whole-file hashes are not computed. `scan.*` diagnostics give observed omission counts for excluded, inaccessible, binary and oversized files or limit exhaustion. When the entry budget stops enumeration, `scan.entryLimit` warns that unvisited entries were not counted and omission totals are **lower bounds**. Cancellation throws `OperationCanceledException`, never returning a partial result as a successful snapshot. Two scans of an unchanged repository yield the same `ContractJson.SerializeSnapshot` output.

**Security boundary:** the caller must authorize the root and run against a trusted, stable, preferably read-only checkout with least-privilege permissions. Managed pre/post-open link checks cannot guarantee atomic no-follow semantics during concurrent, malicious filesystem changes. Future readers of snapshot paths must revalidate containment, permissions, links and byte limits at the actual point of use. A nominally safe text file can still contain secrets. Before any future provider sends content or metadata off-machine, obtain explicit user consent and apply appropriate secret redaction. This phase sends nothing to an AI provider.

## Evidence-backed .NET facts (issue #8)

`RepositoryFactExtractor.Extract(options, cancellationToken)` inventories an explicitly authorized local checkout with `RepositoryScanner`, then inspects only accepted .NET solution, project and source files to populate `RepositorySnapshot.Evidence`. It recognizes .sln/.slnx project listings, declared executable/library/test characteristics, target frameworks, build-time `ProjectReference`, HTTP/worker host signals, Npgsql/RabbitMQ/Redis integration **candidates**, and Docker/compose manifest presence. Facts retain relative file paths and lines where available. A build-time project dependency is not a runtime relationship; an SDK, package or source signal is not proof of a deployed C4 container or a live network connection.

Extraction reads at most **512 KiB per accepted file**, rechecks the authorized root and symlinks, rejects invalid UTF-8/XML DTDs and external entities, and reports changed, inaccessible or unresolvable files via bounded `extract.*` diagnostics. Source text, connection strings, exception text and other potential secrets never enter the output. Managed path checks are not atomic against malicious concurrent filesystem mutations; use a trusted, stable read-only checkout. No external services are contacted. The CLI `inspect` command and MCP transport are not yet implemented.

See [fixtures, v1 snapshot and reproduction](examples/README.md) and [evidence categories](docs/contracts.md).

## Entry point smoke tests

```bash
dotnet run --project src/Repo2C4.Cli/Repo2C4.Cli.csproj -- --help
dotnet run --project src/Repo2C4.Mcp/Repo2C4.Mcp.csproj -- --help
```

CLI help is written to stdout. MCP help and diagnostics are written **only to stderr** to reserve stdout for future MCP JSON-RPC traffic. Unsupported commands exit with code 2; a successful MCP handshake is never simulated. CI additionally validates both executable entrypoints with process-level smoke tests.

## CI and distribution

`.github/workflows/ci.yml` validates locked restore, formatting, Release build, tests, coverage and smoke tests. CodeQL, Dependency Review and optional SonarQube Cloud checks remain available; [Sonar setup](docs/sonarqube-cloud.md) requires `SONAR_TOKEN`.

**Publication is disabled through phase 4:** projects are non-packable, the template's release workflow is removed, and CI produces no NuGet package. Installation and release distribution are defined in phase 5.

See [roadmap #4](https://github.com/rodri-oliveira-dev/Repo2C4/issues/4). All foundation issues #5–#8 share branch `phase/01-foundation`, and a single PR is opened only when the phase is complete.
