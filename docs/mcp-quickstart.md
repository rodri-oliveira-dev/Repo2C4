# MCP client quickstart

[Português (Brasil)](mcp-quickstart.pt-BR.md)

Repo2C4 is a local **stdio** MCP server. The host starts `repo2c4-mcp`; Repo2C4 does not embed or require a proprietary client SDK.

## Install

```bash
dotnet tool install --global Repo2C4.Mcp --version 1.0.0
npm install --global likec4@1.59.4
repo2c4-mcp --help
likec4 --version
```

Authorize only one existing absolute repository root. Do not use a home directory, drive root, or a directory containing unrelated repositories. If the CLI is installed too, `repo2c4 init --repository /absolute/path` and `repo2c4 doctor --repository /absolute/path` can prepare/check the same root without starting analysis or inference.

## Connect

Copy an example from [`examples/mcp-clients/`](../examples/mcp-clients/).

**Portable stdio:** use [`generic.mcp.json`](../examples/mcp-clients/generic.mcp.json) with a compatible host and replace the placeholder with an absolute local path.

**Visual Studio Code:** copy [`vscode.mcp.json`](../examples/mcp-clients/vscode.mcp.json) to `.vscode/mcp.json`. It uses `${workspaceFolder}` as the authorized root. Start/restart with **MCP: List Servers** or the actions in the MCP configuration editor, then confirm the Repo2C4 tools are available in Chat. VS Code owns trust, lifecycle, and any host sandbox; Repo2C4 independently enforces its root boundary. In remote sessions, the server and path resolve where the MCP configuration runs.

**Claude Desktop:** [`claude-desktop.json`](../examples/mcp-clients/claude-desktop.json) shows the local stdio entry. Replace the absolute-path placeholder and merge only the `repo2c4` entry into the existing local MCP configuration. Restart Claude Desktop and check the connected server/tools in its connector/developer surfaces. Claude Desktop now promotes Desktop Extensions for packaged local integrations; Repo2C4 does not ship a proprietary extension here. Web/mobile remote connectors do not replace this local-filesystem flow.

## Evidence-first flow

1. Call `inspect_repository` with a stable repository ID.
2. Page through `get_evidence`; use `get_snapshot` when needed.
3. Propose C1/C2 using only returned evidence IDs; keep unsupported claims under review.
4. Call `generate_likec4` without write authorization first for preview/dry-run.
5. Use `validate_likec4` against an existing generated workspace when applicable.
6. After reviewing the preview, call `generate_likec4` with explicit write authorization and a relative destination inside the root.
7. Validate the written workspace and inspect `get_evidence_report`.

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
| LikeC4 unavailable | Install LikeC4 separately and expose `likec4` on the MCP process PATH. |
| Client rejects configuration | VS Code workspace format uses `servers`; portable/Claude local examples use `mcpServers`. |
| Tools missing after edit | Restart/reload the server or client and inspect its MCP logs. |
| Write conflict | Keep preview first. Repo2C4 blocks unmanaged collisions and changed managed files. |

Never put API keys, tokens, private repository contents, or personal paths in these configuration files.
