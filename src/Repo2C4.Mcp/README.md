# Repo2C4 MCP

<!-- mcp-name: io.github.rodri-oliveira-dev/repo2c4-mcp -->

Repo2C4 MCP is the local stdio Model Context Protocol server for evidence-first, reviewable C4 architecture documentation from authorized .NET repositories.

It exposes bounded repository inspection, evidence retrieval, deterministic LikeC4 generation, evidence reporting and LikeC4 validation. Architectural interpretation remains in the MCP client. The server does not embed an AI provider, accept provider credentials or expose a generic file-reading tool.

## Install

```bash
dotnet tool install --global Repo2C4.Mcp
repo2c4-mcp --help
```

The server requires .NET 10. LikeC4 is installed separately when `validate_likec4` is used.

## Start

An MCP host should launch the server over stdio with the smallest authorized repository root:

```bash
repo2c4-mcp --repository-root "/absolute/path/to/repository"
```

The default tools are:

- `inspect_repository`
- `get_evidence`
- `get_snapshot`
- `get_evidence_report`
- `generate_likec4`
- `validate_likec4`

Remote public Git acquisition is disabled by default and is exposed only with the explicit `--allow-remote-acquisition` host option.

## Safety model

`generate_likec4` is dry-run by default. Filesystem writes require `dryRun=false`, `write=true` and an explicit repository-relative destination. Managed outputs use manifest/hash conflict protection and do not overwrite human edits or unmanaged collisions.

Snapshots are scoped to the current stdio session and expire after 30 minutes.

## Documentation

- MCP server guide: https://github.com/rodri-oliveira-dev/Repo2C4/blob/main/docs/mcp.md
- MCP client quickstart: https://github.com/rodri-oliveira-dev/Repo2C4/blob/main/docs/mcp-quickstart.md
- MCP client workflow: https://github.com/rodri-oliveira-dev/Repo2C4/blob/main/docs/mcp-client.md
- Distribution/security: https://github.com/rodri-oliveira-dev/Repo2C4/blob/main/docs/distribution.md

Source and issues: https://github.com/rodri-oliveira-dev/Repo2C4
