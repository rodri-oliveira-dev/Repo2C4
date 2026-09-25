# MCP stdio server, inspection tools and local access policy

Phase 3 runs Repo2C4 as a local MCP server over stdio. Issue #13 established the transport and filesystem boundary, issue #14 added bounded repository inspection/evidence retrieval, issue #15 added protected LikeC4 generation/validation, and issue #16 documents and tests the complete vendor-neutral client flow.

## Start the server

Build the solution, then pass exactly one authorized local repository root:

```bash
dotnet src/Repo2C4.Mcp/bin/Release/net10.0/Repo2C4.Mcp.dll \
  --repository-root /absolute/path/to/repository
```

As a local configuration alternative, set `REPO2C4_REPOSITORY_ROOT` and omit the command-line option. A command-line root takes precedence over the environment variable. The server requires no AI-provider key, account, network endpoint or model configuration.

## Protocol and logging boundary

The host uses the maintained `ModelContextProtocol.Core` .NET SDK and stdio transport. `stdout` is reserved exclusively for MCP protocol frames. Help, configuration failures and bounded host diagnostics are written to `stderr`; exception details and stack traces are not emitted into the protocol stream.

The process supports cancellation through its host cancellation token and Ctrl+C. Closing stdin ends the stdio session and disposes its in-memory snapshot store. Repository text is treated as untrusted data and cannot replace server instructions. The server embeds no AI provider and exposes no generic file-reading tool.

## Inspection tools

Issue #14 exposes exactly three read-only tools:

| Tool | Purpose | Main inputs |
| --- | --- | --- |
| `inspect_repository` | Inspect an authorized repository directory using the existing Core scanner/extractor and create a session-scoped v1 snapshot. | `repositoryPath` relative to the authorized root, `maxFiles` from 1 to 1,000. |
| `get_evidence` | Retrieve a bounded page of v1 `Evidence`. | `snapshotId`, optional exact `category`, optional metadata-only `pathPrefix`, `pageSize` 1–100, opaque `cursor`. |
| `get_snapshot` | Retrieve bounded snapshot file metadata or diagnostics. It never returns file bodies. | `snapshotId`, `section` = `files` or `diagnostics`, optional `pathPrefix`, `pageSize` 1–100, opaque `cursor`. |

`inspect_repository` returns the snapshot ID, repository/schema IDs, expiry, counts, evidence-category counts, and a small bounded sample of facts/diagnostics. Clients use the retrieval tools for additional pages rather than receiving an entire repository or source tree in one response.

Snapshots live only in the current stdio session and expire after 30 minutes. Snapshot IDs are stable hashes of the canonical v1 snapshot; possession of an ID from another session does not grant access because each session maintains its own store. Pagination cursors are opaque HMAC-authenticated tokens bound to that session, snapshot, tool scope and filters. Modified, cross-filter or out-of-range cursors fail with a controlled `cursor_invalid` error.

## Evidence report tool

Issue #17 adds read-only `get_evidence_report`. It accepts `snapshotId` plus a complete v1 `ArchitectureModel`, verifies the session snapshot binding and returns a bounded summary of the deterministic report: counts, review-required assertion IDs and warning codes. The full Markdown report remains a CLI artifact. The MCP response does not include source bodies, configuration values or absolute paths, does not write files and does not call AI.

See [evidence report and architectural review](evidence-report.md).

## LikeC4 generation and validation tools

Issue #15 adds two tools while keeping architectural interpretation in the MCP client:

| Tool | Purpose | Write behavior |
| --- | --- | --- |
| `generate_likec4` | Accept a complete v1 `ArchitectureModel` plus its session `snapshotId`, verify that the embedded snapshot exactly matches the stored snapshot, enforce review boundaries, and emit deterministic C1/C2 files. Optional `c3ContainerId` adds C3 only for that existing C2 container. | Defaults to `dryRun=true`. Repository writes require `dryRun=false`, `write=true` and an explicit repository-relative `destinationPath`. Existing generated files are never overwritten. |
| `validate_likec4` | Run the existing controlled official LikeC4 CLI adapter against either the proposed generated files or an existing authorized destination directory. | Read-only. Omitting `destinationPath` validates an isolated temporary workspace; providing it validates an existing directory inside the authorized root. |

The server does not accept the model snapshot on trust. `ContractValidator` must accept the model, and the model's canonical v1 snapshot must equal the snapshot identified by `snapshotId` in the current session. This rejects fabricated evidence even when a fabricated model is internally self-consistent.

The MCP boundary also preserves the C4 mapping policy: a C2 container or runtime relation cannot be marked `confirmed` when its support consists only of repository-static `dotnet.*` or `deployment.*` evidence. Such assertions remain `requiresReview`. The client chooses how to interpret evidence and which AI, if any, to use; the server never selects or calls an AI provider.

Protected writes resolve only child directories beneath the configured root, reject traversal and linked existing path components, create files with no-overwrite semantics, and roll back files created by a failed call on a best-effort basis. As with the read boundary, managed checks cannot provide an atomic no-follow guarantee against a concurrently malicious filesystem.

## Controlled errors and limits

The tool descriptions and generated MCP input schemas state their parameters and bounds. Expected controlled errors include:

- `repository_path_invalid` for paths outside the authorized root, missing directories, traversal or linked components;
- `repository_unavailable` for a repository that cannot be inspected safely;
- `max_files_invalid` and `page_size_invalid` for invalid caller limits;
- `snapshot_not_found_or_expired` for missing, expired or cross-session snapshot references;
- `cursor_invalid` for malformed, modified, mismatched or out-of-range cursors;
- `path_filter_invalid`, `category_invalid` and `section_invalid` for invalid filters;
- `model_invalid`, `snapshot_mismatch` and `model_review_required` for unsafe or unsupported architecture models;
- `write_not_authorized`, `destination_required`, `destination_invalid`, `destination_exists` and `write_failed` for protected-write failures;
- `validation_path_invalid` for an unsafe or missing validation directory;
- `tool_timeout` when inspection exceeds 30 seconds;
- `response_limit_exceeded` if a structured tool response would exceed the 1 MiB MCP budget.

The host ceiling remains 1,000 inspected files and 1 MiB per MCP response. Default retrieval pages contain 50 items and are capped at 100.

## Authorized repository root

The configured root must be a fully qualified existing directory without parent traversal and must not itself be a symbolic link, junction or other reparse point. Read/validation paths are relative to this root and must already exist. Protected write destinations may be new child directories, but every existing path component is revalidated and generated files use no-overwrite creation.

The existing Core scanner/extractor remains the data source. It keeps its mandatory sensitive-file exclusions and bounded reads. `.env`, credential/key names and other protected paths are not surfaced. Repository source bodies, README instructions, connection strings and raw secrets are not MCP evidence payloads.

These managed checks reduce accidental escape from the approved checkout but do not claim atomic no-follow guarantees against a concurrently malicious filesystem. Run Repo2C4 against a trusted, stable, preferably read-only checkout with least-privilege permissions.

## Architecture boundary

The MCP project references Core; Core does not reference the MCP SDK. Existing v1 `RepositorySnapshot` and `Evidence` contracts remain the source of truth.

The server performs no architectural inference. Static evidence, hypotheses and confirmed architectural facts remain distinct. `ProjectReference`, package references and categories ending in `.candidate` remain static leads only and do not become confirmed runtime relationships when exposed over MCP. Architectural interpretation remains the MCP client's responsibility.

Issue #15 reuses the Core emitter and validator without adding AI, Git operations, push/PR automation or semantic editing of existing documentation. Issue #16 adds generic client configuration, reusable review prompts and a deterministic C1/C2 protocol-client reproduction. See [MCP client workflow](mcp-client.md).


## Selective C3

Issue #18 keeps the stable C1/C2 model unchanged and adds a compatible C3 extension scoped to one explicitly selected C2 container. `generate_likec4` accepts optional `c3ContainerId`; omission preserves the existing C1/C2 behavior. A valid selection derives a bounded component proposal only from evidence already referenced by that container and emits `components.c4` plus `c3.views.c4`. Other containers do not receive C3 automatically. Candidate/static evidence remains `requiresReview`, and missing container, unsupported evidence, schema mismatch and C3 size limits fail with controlled contract errors.
