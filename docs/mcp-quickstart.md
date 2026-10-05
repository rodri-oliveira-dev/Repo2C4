# MCP client quickstart

[Português (Brasil)](mcp-quickstart.pt-BR.md)

Repo2C4 is a local **stdio** MCP server. The host starts `repo2c4-mcp`; Repo2C4 does not embed or require a proprietary client SDK.

## Install

After the public package is available:

```bash
dotnet tool install --global Repo2C4.Mcp
npm install --global likec4@1.59.4
repo2c4-mcp --help
likec4 --version
```

LikeC4 is required for `validate_likec4`; inspection and evidence retrieval do not require an AI account or cloud credential.

Before a public package release, build a trusted checkout and configure the client to run the Release DLL through `dotnet`.

Authorize only one existing absolute repository root. Do not use a home directory, drive root, or a directory containing unrelated repositories. If the CLI is installed too, `repo2c4 init --repository /absolute/path` and `repo2c4 doctor --repository /absolute/path` can prepare/check the same root without starting analysis or inference.

## Connect

Copy an example from [`examples/mcp-clients/`](../examples/mcp-clients/). The checked-in examples keep remote acquisition **disabled by default**; add `--allow-remote-acquisition` only when public remote Git inspection is intentionally required.

**Portable stdio:** use [`generic.mcp.json`](../examples/mcp-clients/generic.mcp.json) with a compatible host and replace the placeholder with an absolute local path.

**Visual Studio Code:** copy [`vscode.mcp.json`](../examples/mcp-clients/vscode.mcp.json) to `.vscode/mcp.json`. It uses `${workspaceFolder}` as the authorized root. Start/restart with **MCP: List Servers** or the actions in the MCP configuration editor, then confirm the Repo2C4 tools are available in Chat. VS Code owns trust, lifecycle, and any host sandbox; Repo2C4 independently enforces its root boundary. In remote sessions, the server and path resolve where the MCP configuration runs.

**Claude Desktop:** [`claude-desktop.json`](../examples/mcp-clients/claude-desktop.json) shows the local stdio entry. Replace the absolute-path placeholder and merge only the `repo2c4` entry into the existing local MCP configuration. Restart Claude Desktop and check the connected server/tools in its connector/developer surfaces. Claude Desktop now promotes Desktop Extensions for packaged local integrations; Repo2C4 does not ship a proprietary extension here. Web/mobile remote connectors do not replace this local-filesystem flow.

## First safe call

After the client connects, inspect the authorized root itself:

```json
{
  "repositoryPath": ".",
  "integrationReportPath": "artifacts/inspection.json",
  "maxFiles": 1000
}
```

`integrationReportPath` is optional. When present, it must be relative to the selected repository and identify a DotNetRepoInspector `InspectionReport` schema `1.6+` file. Repo2C4 never starts DotNetRepoInspector and never exposes the report as a generic file reader. Omit the field to preserve the original inspection behavior.

`inspect_repository` returns `snapshotId`, `repositoryId`, expiry/count metadata and bounded evidence/diagnostic summaries. Imported facts use `external.*` category counts and belong to that same session snapshot. `repositoryId` is returned by the tool; it is **not** an input. Keep `snapshotId` for subsequent calls in the same stdio session. Snapshots expire after 30 minutes and do not survive reconnects.

## Evidence-first flow

1. Call `inspect_repository` with `repositoryPath="."` or a safe repository-relative child directory.
2. Page through `get_evidence`; use `get_snapshot` for file metadata/diagnostics when needed.
3. Build or review a complete v1 `ArchitectureModel` using only the session snapshot/evidence.
4. Call `get_evidence_report` to surface review-required assertions.
5. Call `generate_likec4` with the default `dryRun=true`; provide the intended `destinationPath` when you want a destination-aware change plan.
6. Call `validate_likec4` **without** `destinationPath` to validate the proposal in an isolated temporary workspace.
7. Review generated files, changes, `requiresReview` assertions and validation diagnostics.
8. Only after explicit local approval, call `generate_likec4` with `dryRun=false`, `write=true` and the exact repository-relative destination.
9. Call `validate_likec4` again with the written `destinationPath`.

For selective C3, new clients should pass only the evidence-supported C2 container IDs in `c3Containers` to both proposal generation and proposal validation, for example `["container_api", "container_worker"]`. The collection is bounded to 8 selections and duplicates are deduplicated deterministically. Legacy `c3ContainerId` remains supported for one container.

Clients do not reconstruct or submit internal Semantic C3 facts. The server retains the canonical bounded facts produced by inspection and uses them only after the submitted public snapshot passes freshness validation. When active, Semantic C3 responses include generated `c3Views` and `semantic-c3-evidence-report.md`. See [Semantic C3](semantic-c3.md).

The host selects the AI model. Repo2C4 MCP has no cloud-provider key and does not promote hypotheses to facts.

## Reproducible smoke

For a safe first connection, authorize the absolute path to `examples/fixtures/library-only` in a Repo2C4 checkout. Confirm `inspect_repository` returns evidence only from that fixture. Preview before write. For deterministic comparison, use the reviewed models and outputs under `examples/end-to-end/`.

Proprietary client applications are not launched by CI. CI validates the example JSON and security invariants; connection/reload in each application is the documented manual smoke.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Server/command not found | Check `dotnet tool list --global`, the client process PATH, and `repo2c4-mcp --help`. |
| Invalid repository path | The root must be an existing absolute directory and not a symlink/junction/reparse-point root. |
| Access outside root rejected | Intentional. Keep the smallest required root; never broaden it to bypass policy. |
| Integration report rejected | Keep it inside the selected repository, use a relative non-linked path, and generate compatible schema `1.6+` JSON. |
| LikeC4 unavailable | Install LikeC4 separately and expose `likec4` on the MCP process PATH. |
| Client rejects configuration | VS Code workspace format uses `servers`; portable/Claude local examples use `mcpServers`. |
| Tools missing after edit | Restart/reload the server or client and inspect its MCP logs. |
| Snapshot missing after reconnect | Run `inspect_repository` again; snapshots are session-scoped and expire after 30 minutes. |
| `snapshot_mismatch` | Rebuild/review the model against the exact current-session snapshot. |
| Write conflict | Preserve the human edit, run another destination-aware dry-run and review the new plan. |
| Remote tool missing | Expected unless `--allow-remote-acquisition` was explicitly configured. |

Never put API keys, tokens, private repository contents, or personal paths in these configuration files.

## Optional public remote repository

Remote acquisition is intentionally absent from the default client examples. Start the MCP server with `--allow-remote-acquisition` only when needed to explicitly expose `inspect_remote_repository` for a public HTTPS Git URL and optional ref. Remote acquisition is disabled by default. It acquires the repository into an isolated temporary workspace, rejects credentials/submodules/links, runs the same evidence scanner, stores only the bounded snapshot in the MCP session and deletes the workspace. The tool returns sanitized acquisition provenance (URL, requested ref and resolved commit) separately from architectural evidence. Local `inspect_repository` remains the default and does not require network access.
