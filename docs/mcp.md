# MCP stdio server and local access policy

Issue #13 establishes only the local MCP transport and filesystem boundary. It does not expose repository inspection, evidence retrieval or LikeC4 generation tools. Those capabilities remain scoped to later Phase 3 issues.

## Start the server

Build the solution, then pass exactly one authorized local repository root:

```bash
dotnet src/Repo2C4.Mcp/bin/Release/net10.0/Repo2C4.Mcp.dll \
  --repository-root /absolute/path/to/repository
```

As a local configuration alternative, set `REPO2C4_REPOSITORY_ROOT` and omit the command-line option. A command-line root takes precedence over the environment variable.

The server requires no AI-provider key, account, network endpoint or model configuration.

## Protocol and logging boundary

The host uses the maintained `ModelContextProtocol.Core` .NET SDK and stdio transport. `stdout` is reserved exclusively for MCP protocol frames. Help, configuration failures and bounded host diagnostics are written to `stderr`; unhandled exception details and stack traces are not emitted into the protocol stream.

The process supports cancellation through its host cancellation token and Ctrl+C. Closing stdin ends the stdio session cleanly.

Issue #13 registers an empty MCP tool collection on purpose. A client can initialize and call `tools/list`, but receives an empty tool list. `inspect_repository`, evidence retrieval and LikeC4 operations are not introduced here.

## Authorized repository root

The configured root must:

- be a fully qualified absolute path;
- already exist as a directory;
- contain no `..` parent traversal;
- not itself be a symbolic link, junction or other reparse point.

Paths accepted by future MCP tools must be relative to that root. Resolution rejects absolute paths, parent traversal, paths that normalize outside the root, nonexistent paths and any symbolic/reparse-link component. Link traversal is rejected even when the link target would remain inside the root; this deliberately keeps the MCP boundary aligned with the existing Core scanner's conservative local-filesystem policy.

These managed checks reduce accidental escape from the approved checkout but do not claim atomic no-follow guarantees against a concurrently malicious filesystem. Run Repo2C4 against a trusted, stable, preferably read-only checkout with least-privilege permissions.

## Host limits established by issue #13

The Phase 3 host policy defines:

- initialization timeout: 30 seconds;
- tool execution timeout ceiling: 30 seconds;
- inspection file ceiling: 1,000 files, aligned with the existing Core scanner default;
- MCP response ceiling: 1,048,576 bytes.

Only the initialization timeout is exercised by the transport in issue #13 because no domain tools exist yet. Issues that add tools must consume the declared tool/file/response limits rather than inventing parallel limits.

## Architecture boundary

The MCP project references Core; Core does not reference the MCP SDK. Existing v1 contracts remain the source of truth.

The server performs no architectural inference and embeds no AI provider or model selection. Static evidence, hypotheses and confirmed architectural facts remain distinct. `ProjectReference`, packages and `.candidate` signals do not become confirmed runtime relationships merely because they are exposed through MCP in later issues. Architectural interpretation remains the MCP client's responsibility.
