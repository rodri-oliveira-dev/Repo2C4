# Using Repo2C4 from an MCP client

Repo2C4 Phase 3 is a local stdio MCP server. The server collects bounded repository evidence, accepts a client-proposed C1/C2 `ArchitectureModel`, previews deterministic LikeC4, validates it with the official LikeC4 CLI, and writes only after explicit authorization.

The MCP server contains no AI SDK, provider credentials or model selector. If an AI model interprets the evidence, that model is selected and executed by the MCP client.

## Prerequisites

Build Repo2C4 first:

```bash
dotnet restore Repo2C4.slnx --locked-mode
dotnet build Repo2C4.slnx --configuration Release --no-restore
```

For `validate_likec4`, install the documented validation runtime. CI uses Node.js `22.23.3` and `likec4@1.59.4`:

```bash
npm install --global --no-audit --no-fund likec4@1.59.4
likec4 --version
```

Inspection, evidence retrieval, preview and protected writes require no cloud account or AI credential.

## Generic stdio client configuration

MCP clients use different settings files and UI labels. Configure a local stdio server using the equivalent of this generic shape, replacing every placeholder with an absolute local path:

```json
{
  "mcpServers": {
    "repo2c4": {
      "command": "dotnet",
      "args": [
        "/ABSOLUTE/PATH/TO/Repo2C4.Mcp.dll",
        "--repository-root",
        "/ABSOLUTE/PATH/TO/AUTHORIZED/REPOSITORY"
      ]
    }
  }
}
```

The built executable is normally:

```text
src/Repo2C4.Mcp/bin/Release/net10.0/Repo2C4.Mcp.dll
```

The configured repository root is the security boundary. Tool paths and write destinations are relative to that root. Do not configure a broader directory merely for convenience.

An environment-variable form is also supported. Set `REPO2C4_REPOSITORY_ROOT` and omit `--repository-root`; an explicit command-line root takes precedence.

## Reusable client instruction

A reusable natural-language instruction is checked in at:

- `examples/mcp-client/architecture-review-prompt.md`
- Portuguese translation: `examples/mcp-client/architecture-review-prompt.pt-BR.md`

The instruction requires the client to:

1. call `inspect_repository`;
2. page evidence with `get_evidence`/`get_snapshot` when needed;
3. distinguish observed evidence from architectural hypotheses;
4. propose C1 and C2 as v1 `ArchitectureModel` values;
5. keep unsupported boundaries/relations as `requiresReview`;
6. call `generate_likec4` in dry-run mode first;
7. call `validate_likec4`;
8. request explicit user approval before any `write=true` call.

A client must not infer that `ProjectReference`, package presence or a `.candidate` category proves runtime communication or a deployment boundary.

## Deterministic reproduction without AI

The repository contains a vendor-neutral protocol test that exercises the entire Phase 3 flow without a hosted model or proprietary MCP client:

```bash
dotnet test tests/Repo2C4.Mcp.Tests/Repo2C4.Mcp.Tests.csproj \
  --configuration Release \
  --no-build
```

`McpClientEndToEndTests.ProtocolClientReproducesVersionedC1AndC2Flow`:

1. copies `examples/fixtures/library-only` to an isolated temporary checkout;
2. starts the real `Repo2C4.Mcp` executable over stdio;
3. performs the MCP initialize handshake;
4. calls `inspect_repository`;
5. submits the versioned models `examples/end-to-end/architecture.c1.v1.json` and `architecture.c2.v1.json`;
6. previews both models and verifies that no files were written;
7. compares preview content with the checked-in C1/C2 golden LikeC4 files;
8. writes C1 and C2 only with `dryRun=false` plus `write=true`;
9. compares every written `specification.c4`, `model.c4` and `views.c4` with the checked-in goldens;
10. when `REPO2C4_LIKEC4_INTEGRATION=1`, validates both proposed and written workspaces through the real LikeC4 CLI.

CI runs the protocol client without any paid AI dependency, then repeats the suite after installing the pinned LikeC4 CLI with `REPO2C4_LIKEC4_INTEGRATION=1`.

## Expected write sequence

A safe interactive client flow is:

```text
inspect_repository
  -> get_evidence / get_snapshot as needed
  -> client proposes ArchitectureModel C1/C2
  -> generate_likec4 (dryRun=true)
  -> validate_likec4
  -> user reviews preview and diagnostics
  -> user explicitly approves a relative destination
  -> generate_likec4 (dryRun=false, write=true)
  -> validate_likec4(destinationPath=...)
```

Writing is not implied by asking for analysis or validation. Existing generated files are never overwritten by the MCP tool.

## Interpretation limits

Repo2C4 evidence is repository evidence, not runtime observation. A source/API/package signal can support a hypothesis while remaining insufficient to confirm deployment, ownership or communication.

The MCP server enforces the v1 contract and some review boundaries, but it does not decide that a proposed architecture is semantically correct. Human review remains required for material architectural claims.

The existing Phase 2 CLI is an offline `inspect`/`generate`/`validate` host that consumes an already supplied model. A future direct AI-assisted CLI workflow, in which a CLI itself selects/calls an interpretation provider, is not part of Phase 3.
