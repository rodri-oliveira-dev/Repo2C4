# MCP stdio server, inspection tools and local access policy

Phase 3 runs Repo2C4 as a local MCP server over stdio. Issue #13 established the transport and filesystem boundary; issue #14 exposes only bounded repository inspection and evidence retrieval. LikeC4 generation/validation tools remain out of scope until issue #15.

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

## Controlled errors and limits

The tool descriptions and generated MCP input schemas state their parameters and bounds. Expected controlled errors include:

- `repository_path_invalid` for paths outside the authorized root, missing directories, traversal or linked components;
- `repository_unavailable` for a repository that cannot be inspected safely;
- `max_files_invalid` and `page_size_invalid` for invalid caller limits;
- `snapshot_not_found_or_expired` for missing, expired or cross-session snapshot references;
- `cursor_invalid` for malformed, modified, mismatched or out-of-range cursors;
- `path_filter_invalid`, `category_invalid` and `section_invalid` for invalid filters;
- `tool_timeout` when inspection exceeds 30 seconds;
- `response_limit_exceeded` if a structured tool response would exceed the 1 MiB MCP budget.

The host ceiling remains 1,000 inspected files and 1 MiB per MCP response. Default retrieval pages contain 50 items and are capped at 100.

## Authorized repository root

The configured root must be a fully qualified existing directory without parent traversal and must not itself be a symbolic link, junction or other reparse point. Tool repository paths are relative to this root. Resolution rejects absolute paths, traversal, normalization outside the root, nonexistent paths and symbolic/reparse-link components.

The existing Core scanner/extractor remains the data source. It keeps its mandatory sensitive-file exclusions and bounded reads. `.env`, credential/key names and other protected paths are not surfaced. Repository source bodies, README instructions, connection strings and raw secrets are not MCP evidence payloads.

These managed checks reduce accidental escape from the approved checkout but do not claim atomic no-follow guarantees against a concurrently malicious filesystem. Run Repo2C4 against a trusted, stable, preferably read-only checkout with least-privilege permissions.

## Architecture boundary

The MCP project references Core; Core does not reference the MCP SDK. Existing v1 `RepositorySnapshot` and `Evidence` contracts remain the source of truth.

The server performs no architectural inference. Static evidence, hypotheses and confirmed architectural facts remain distinct. `ProjectReference`, package references and categories ending in `.candidate` remain static leads only and do not become confirmed runtime relationships when exposed over MCP. Architectural interpretation remains the MCP client's responsibility.

LikeC4 generation, validation and protected writes are intentionally absent from issue #14 and remain scoped to issue #15.
