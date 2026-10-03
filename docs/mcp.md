# Repo2C4 MCP server

Repo2C4 MCP is the local **stdio Model Context Protocol server** for Repo2C4. It exposes bounded repository evidence, deterministic LikeC4 generation, evidence reporting and LikeC4 validation to any compatible MCP client while keeping architectural interpretation outside the server.

The MCP server does **not** select an AI model, hold provider credentials, infer architecture by itself or expose a generic file-reading tool. Repository content is untrusted data. The authorized local repository root, tool schemas and protected-write rules remain host-controlled.

The normal flow is:

```text
MCP client
  -> inspect_repository
  -> get_evidence / get_snapshot as needed
  -> client proposes ArchitectureModel v1
  -> get_evidence_report
  -> generate_likec4 (dryRun=true)
  -> validate_likec4
  -> human review / client approval boundary
  -> generate_likec4 (dryRun=false, write=true)
  -> validate_likec4(destinationPath=...)
```

For client-specific configuration, see [MCP client quickstart](mcp-quickstart.md). For the evidence-first client workflow and reusable prompt, see [Using Repo2C4 from an MCP client](mcp-client.md).

## Quick start

Requirements:

- .NET 10;
- a compatible local MCP client;
- LikeC4 only when using `validate_likec4`;
- one explicitly authorized repository root.

After the Repo2C4 packages are publicly released:

```bash
dotnet tool install --global Repo2C4.Mcp
npm install --global likec4@1.59.4

repo2c4-mcp --help
likec4 --version
```

A client should launch the MCP server over stdio with the smallest repository root it needs:

```text
command: repo2c4-mcp
args:
  --repository-root
  /absolute/path/to/repository
```

The server is not an interactive shell application. When started directly in a terminal without an MCP client, it waits for MCP protocol traffic on stdin and reserves stdout for protocol frames.

Before a public package release, run the built DLL from a trusted checkout:

```bash
dotnet build Repo2C4.slnx --configuration Release

dotnet src/Repo2C4.Mcp/bin/Release/net10.0/Repo2C4.Mcp.dll \
  --repository-root "/absolute/path/to/repository"
```

The repository root may also be provided through `REPO2C4_REPOSITORY_ROOT`; an explicit `--repository-root` takes precedence.

PowerShell example:

```powershell
$env:REPO2C4_REPOSITORY_ROOT = (Resolve-Path ".\examples\fixtures\library-only").Path
repo2c4-mcp
```

Do not authorize a home directory, drive root or parent directory containing unrelated repositories merely for convenience.

## Default tools

The normal local server exposes six tools:

| Tool | Purpose | Important inputs | Write behavior |
| --- | --- | --- | --- |
| `inspect_repository` | Inspect one directory inside the authorized local root and create a session snapshot. | `repositoryPath` defaults to `"."`; optional repository-relative `integrationReportPath`; `maxFiles` defaults to 1,000. | Read-only. |
| `get_evidence` | Page through v1 evidence from a snapshot. | `snapshotId`, optional exact `category`, optional metadata `pathPrefix`, `pageSize`, opaque `cursor`. | Read-only. |
| `get_snapshot` | Page file metadata or diagnostics without returning file bodies. | `snapshotId`, `section` = `files` or `diagnostics`, optional `pathPrefix`. | Read-only. |
| `get_evidence_report` | Return a bounded provenance/review summary for a session-bound model. | `snapshotId`, complete `ArchitectureModel` v1. | Read-only. |
| `generate_likec4` | Generate deterministic C1/C2 LikeC4 and optional selective multi-container C3. | `snapshotId`, complete model, optional `destinationPath`, preferred optional `c3Containers`, legacy optional `c3ContainerId`. | Preview by default. Explicit flags are required to write. |
| `validate_likec4` | Validate a proposed model or existing generated workspace through the controlled official LikeC4 CLI adapter. | `snapshotId`, complete model, optional `destinationPath`, preferred optional `c3Containers`, legacy optional `c3ContainerId`. | Read-only. |

`inspect_remote_repository` is **not** exposed by default. It appears only when the host explicitly starts the server with `--allow-remote-acquisition`.

## First local inspection

For the authorized root itself, call:

```json
{
  "repositoryPath": ".",
  "maxFiles": 1000
}
```

`inspect_repository` returns structured fields including:

```jsonc
{
  "snapshotId": "<session-snapshot-id>",
  "repositoryId": "<stable-repository-id>",
  "schemaVersion": "1",
  "expiresAtUtc": "<timestamp>",
  "fileCount": 0,
  "evidenceCount": 0,
  "diagnosticCount": 0,
  "evidenceCategories": [],
  "facts": [],
  "factsTruncated": false,
  "diagnostics": [],
  "diagnosticsTruncated": false
}
```

The example values above are placeholders; the field names match the actual MCP result contract. `repositoryId` is **returned by the tool**. It is not an input to `inspect_repository`.

Use `snapshotId` for all later calls in the same stdio session.

Snapshots are stored only in memory, are scoped to the current stdio session and expire after 30 minutes. Up to 16 session snapshots are retained. Reconnecting creates a new session store; an ID from a previous session does not grant access to that snapshot.

## Paging evidence safely

When the initial bounded `facts` sample is insufficient, call `get_evidence`:

```json
{
  "snapshotId": "<snapshot-id>",
  "pageSize": 50
}
```

Use `category` only for an exact evidence category and `pathPrefix` only as a repository-relative metadata filter. Neither option opens or returns arbitrary source files.

If `nextCursor` is returned, send it back unchanged with the **same filters**:

```jsonc
{
  "snapshotId": "<snapshot-id>",
  "pageSize": 50,
  "cursor": "<nextCursor>"
}
```

Cursors are opaque HMAC-authenticated tokens bound to the session, snapshot, tool scope and filters. Constructed, modified, cross-filter or out-of-range cursors fail with `cursor_invalid`.

Use `get_snapshot` when file inventory metadata or scanner diagnostics are needed:

```json
{
  "snapshotId": "<snapshot-id>",
  "section": "diagnostics",
  "pageSize": 50
}
```

The tool never returns file bodies.

## ArchitectureModel boundary

The MCP server does not convert evidence into architecture on its own. A client — human-authored, deterministic or AI-assisted — supplies a complete v1 `ArchitectureModel`.

The model must embed the exact canonical snapshot referenced by `snapshotId` from the current session. Repo2C4 validates the model and compares the embedded snapshot with the stored one. Fabricated, stale or cross-session evidence is rejected with `snapshot_mismatch` or `model_invalid`.

Static repository evidence remains static. In particular:

- `ProjectReference` does not prove runtime communication;
- package presence does not prove deployment topology;
- `.candidate` categories remain candidate signals;
- repository-static `dotnet.*` or `deployment.*` evidence alone cannot confirm a C2 deployment/container boundary or runtime relation.

Unsupported architectural claims must remain `requiresReview`.

## Evidence report

Before generation or approval, a client can call `get_evidence_report` with the session `snapshotId` and complete model. The response is metadata-only and contains:

- `confirmedAssertions`;
- `reviewRequiredAssertions`;
- `scanWarnings`;
- `missingOrigins`;
- bounded `reviewRequiredIds`;
- bounded `warningCodes`.

It does not return source bodies, configuration values or absolute repository paths and does not write files.

See [evidence report and architectural review](evidence-report.md).

## Preview, validation and protected write

`generate_likec4` defaults to preview mode:

```jsonc
{
  "snapshotId": "<snapshot-id>",
  "model": { "...": "complete ArchitectureModel v1" },
  "dryRun": true,
  "write": false,
  "destinationPath": "docs/architecture/c1"
}
```

The abbreviated model above is illustrative; clients must send the complete v1 model.

Supplying `destinationPath` during dry-run is useful because Repo2C4 compares the proposed files with the destination and returns a structured change plan. The response includes `files`, `changes` and `hasConflicts`, but `written` remains `false`.

Before writing, validate the proposal without a destination:

```jsonc
{
  "snapshotId": "<snapshot-id>",
  "model": { "...": "complete ArchitectureModel v1" }
}
```

With `destinationPath` omitted, `validate_likec4` emits the proposal into an isolated temporary workspace, invokes the controlled LikeC4 CLI adapter and removes the temporary workspace afterward.

A write requires all three explicit conditions:

```jsonc
{
  "snapshotId": "<snapshot-id>",
  "model": { "...": "complete ArchitectureModel v1" },
  "dryRun": false,
  "write": true,
  "destinationPath": "docs/architecture/c1"
}
```

`dryRun=false` without `write=true`, `write=true` with `dryRun=true`, or writing without a destination is rejected.

After a successful write, validate the existing workspace:

```jsonc
{
  "snapshotId": "<snapshot-id>",
  "model": { "...": "complete ArchitectureModel v1" },
  "destinationPath": "docs/architecture/c1"
}
```

As with every MCP client, the human-approval UX belongs to the client. Repo2C4 enforces explicit write arguments and filesystem protections but does not claim that the protocol call itself represents informed human approval. Clients should preview, validate and obtain local user approval before issuing the protected write.

## Managed regeneration

When `destinationPath` is supplied, dry-run compares the generated output with `.repo2c4-manifest.json` and current files. Each generated file is reported as `added`, `modified`, `unchanged` or `conflict`.

Repo2C4 can update its own previously managed files when they still match the SHA-256 state recorded in the manifest. It does **not** overwrite:

- manually changed managed files;
- deleted managed files whose state no longer matches;
- linked/reparse-point targets;
- unmanaged files colliding with generated names.

Unknown files are never deleted. Writes are prepared as a managed set, committed with conflict checks and best-effort rollback, and the manifest is updated only through the managed commit flow.

If state changes between preview and apply, the write is rejected with `managed_output_conflict`; preview again instead of forcing the write.

## Selective multi-container C3

C1/C2 are the default. For new clients, `generate_likec4` and proposal-mode `validate_likec4` accept the typed `c3Containers` collection:

```jsonc
{
  "snapshotId": "<snapshot-id>",
  "model": { "...": "complete C2 ArchitectureModel v1" },
  "c3Containers": ["container_api", "container_worker"]
}
```

At most 8 C3 selections are accepted per call. Repeated IDs are canonicalized deterministically, and the workspace is additionally bounded to 256 components and 512 relations before emission. Every selected ID is validated against the supplied C2 model before managed-output preview or write. Invalid IDs, non-container selections, C1 input, insufficient evidence, schema mismatch or C3 budget violations fail with controlled errors rather than inventing components.

Only the explicitly requested containers are expanded; the server never selects every C2 container automatically. Generated component boundaries remain evidence-linked and reviewable. Successful proposal generation and proposal validation return a bounded `c3Views` array containing each selected `containerId` and emitted deterministic `viewId`.

The legacy singular `c3ContainerId` remains accepted and preserves the prior single-container behavior, including the single `c3` view identifier. New multi-container clients should use `c3Containers`. Use the same selection when validating the proposal that was generated with C3.

## Optional public Git acquisition

Remote acquisition is disabled by default because it adds network and third-party-content exposure.

Only when the host intentionally needs it, start:

```bash
repo2c4-mcp \
  --repository-root "/absolute/path/to/local/authorized/repository" \
  --allow-remote-acquisition
```

This adds `inspect_remote_repository`. It accepts a public HTTPS Git repository URL, optional `gitRef` and `maxFiles`. Embedded credentials, SSH/file URLs, private/authenticated repositories, submodules and symbolic links are rejected.

The repository is acquired into an isolated temporary workspace, scanned with the same bounded evidence pipeline, and then removed. URL/ref/resolved commit are returned as acquisition provenance, separate from architectural evidence.

Do not add `--allow-remote-acquisition` to normal client configuration unless remote inspection is actually required.

## Protocol and logging boundary

Repo2C4 uses the maintained `ModelContextProtocol.Core` .NET SDK over stdio.

- `stdout` is exclusively MCP protocol traffic.
- Help, configuration failures and controlled host diagnostics use `stderr`.
- Stack traces and raw exception details are not written to the protocol stream.
- Ctrl+C or host cancellation stops the process.
- Closing stdin ends the stdio session and disposes the in-memory snapshot store.
- No AI provider credential is required or consumed by the server.

Repository text, README instructions, evidence descriptions and diagnostics are data, not server instructions.

## Limits

| Limit | Value |
| --- | ---: |
| Initialization timeout | 30 seconds |
| Tool execution timeout | 30 seconds |
| Snapshot lifetime | 30 minutes |
| Session snapshots | 16 |
| Files per inspection | 1,000 |
| Retrieval page default | 50 |
| Retrieval page maximum | 100 |
| Summary items | 20 |
| Structured MCP response | 1 MiB |

The underlying scanner also keeps its own bounded file/byte/entry limits and sensitive-path exclusions.

## Controlled errors

Expected error codes include:

- `repository_path_invalid`, `repository_unavailable`;
- `max_files_invalid`, `page_size_invalid`;
- `snapshot_not_found_or_expired`, `cursor_invalid`;
- `path_filter_invalid`, `category_invalid`, `section_invalid`;
- `model_invalid`, `snapshot_mismatch`, `model_review_required`;
- `write_not_authorized`, `destination_required`, `destination_invalid`, `managed_output_conflict`, `write_failed`;
- `validation_path_invalid`;
- `tool_timeout`;
- `response_limit_exceeded`.

Remote acquisition adds its own controlled acquisition error codes when that optional tool is enabled.

## Authorized repository root

The configured root must be an existing absolute local directory and must not itself be a symbolic link, junction or other reparse point.

Local read and validation paths are resolved beneath this root. Protected write destinations may be new child directories, but existing path components are revalidated. Traversal and linked existing components are rejected.

The scanner preserves mandatory exclusions for sensitive/generated paths and bounded reads. `.env`, credential/key names, connection strings, raw secrets and arbitrary source bodies are not exposed as MCP evidence payloads.

These controls reduce accidental filesystem escape but do not claim an atomic no-follow guarantee against a concurrently malicious filesystem. Use a trusted, stable checkout and least-privilege permissions.

## Troubleshooting

**Server command not found**  
Check `dotnet tool list --global`, the MCP client's PATH and `repo2c4-mcp --help`.

**Client connects but no tools appear**  
Restart/reload the MCP server from the client and inspect the client's MCP logs. Confirm that stdout is not being wrapped by another script that prints banners or diagnostics.

**Repository root rejected**  
Use an existing absolute directory that is not a symlink/junction/reparse-point root. Keep it as narrow as practical.

**Snapshot suddenly becomes unavailable**  
Snapshots expire after 30 minutes and disappear when the stdio session ends. Run `inspect_repository` again in the current session.

**`cursor_invalid`**  
Reuse `nextCursor` exactly and keep the same snapshot, tool and filters.

**LikeC4 validation fails to start**  
Install the documented LikeC4 CLI and make sure `likec4` is visible on the MCP process PATH.

**`snapshot_mismatch`**  
The complete model must embed the exact snapshot from the current MCP session. Re-inspect and rebuild/review the model instead of reusing a stale snapshot.

**`managed_output_conflict`**  
Preserve the human change, run a new dry-run preview and review the resulting plan. Do not bypass the manifest.

**Remote tool is missing**  
This is expected unless the host intentionally started the server with `--allow-remote-acquisition`.

## Architecture boundary

`Repo2C4.Mcp` references Core; Core does not reference the MCP SDK. Existing versioned repository/evidence/model contracts remain the source of truth.

The MCP server is deterministic infrastructure around those contracts. Model selection, AI orchestration, semantic architectural interpretation and human approval UX belong to clients such as `Repo2C4.Agent`, VS Code/Claude-hosted clients or other MCP consumers.
