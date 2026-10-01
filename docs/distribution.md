# Repo2C4 distribution and verified release

Repo2C4 v1.0.0 ships three **separate .NET 10 tool packages**: `Repo2C4.Cli` (command `repo2c4`), `Repo2C4.Mcp` (command `repo2c4-mcp`) and `Repo2C4.Agent` (command `repo2c4-agent`). The shared Core remains an internal project reference, not a separately published package. The Agent is a client of the MCP executable over stdio and does not take a project/domain dependency on Core or MCP. Package version comes from `Directory.Build.props`; the three products use the same version and distinct, non-placeholder package IDs. The tools target .NET 10, so a compatible .NET runtime/SDK must be installed. LikeC4 is an **independent, externally installed** validator and renderer, not bundled inside Repo2C4.

## Install the published tools

Published releases expose all three product tools through NuGet.org. Install the exact release version:

```bash
dotnet tool install --global Repo2C4.Cli --version 1.0.0
dotnet tool install --global Repo2C4.Mcp --version 1.0.0
dotnet tool install --global Repo2C4.Agent --version 1.0.0
repo2c4 --help
repo2c4-mcp --help
repo2c4-agent --help
```

LikeC4 is still an independent dependency for validation/rendering and must be installed separately.

## Public distribution surfaces

The release uses different channels for artifact hosting and product discovery:

| Product | Artifact distribution | Discovery |
| --- | --- | --- |
| `Repo2C4.Cli` | NuGet.org + GitHub Release | GitHub/NuGet |
| `Repo2C4.Mcp` | NuGet.org + GitHub Release | Official MCP Registry metadata in `server.json` |
| `Repo2C4.Agent` | NuGet.org + GitHub Release | GitHub/NuGet |

The Agent is intentionally not published as an MCP server or platform-specific agent package. It remains a local .NET Tool and an independent MCP client.

### Official MCP Registry

The repository contains a versioned root `server.json` for the MCP Registry name:

```text
io.github.rodri-oliveira-dev/repo2c4-mcp
```

It points to the public NuGet package `Repo2C4.Mcp`, uses `dnx` as the .NET 10 runtime hint, stdio transport and declares `--repository-root` as a required local `filepath` argument. Remote acquisition is deliberately not part of the registry's default execution metadata.

NuGet ownership verification depends on the package README containing this exact marker:

```html
<!-- mcp-name: io.github.rodri-oliveira-dev/repo2c4-mcp -->
```

The marker is packaged from `src/Repo2C4.Mcp/README.md`. The distribution smoke verifies the registry name, package/version binding, required root argument and ownership marker before a release can proceed.

The Official MCP Registry stores metadata rather than the NuGet artifact, so the matching NuGet package must be public first. The protected release workflow enforces that ordering: it publishes GitHub Release/NuGet, verifies that the public `Repo2C4.Mcp` package can be installed, and only then runs the registry publication job.

Registry authentication uses the Official MCP Registry's GitHub Actions OIDC flow. The registry job receives only `contents: read` and `id-token: write`; it does not require a PAT or long-lived MCP Registry secret. The job downloads a pinned `mcp-publisher` release, verifies its SHA-256, validates `server.json`, checks whether that exact server/version already exists, publishes only when absent, and verifies that the version becomes readable through the registry API.

For local validation or recovery, maintainers may still run:

```bash
mcp-publisher validate server.json
mcp-publisher login github
mcp-publisher publish server.json
```

A duplicate immutable version must not be republished. If the registry job fails after NuGet/GitHub publication, rerun the failed registry job from the same released commit; do not rebuild or change `server.json` for that version.

When preparing a later Repo2C4 release, update `Directory.Build.props` and both version fields in `server.json` to the same exact SemVer before running distribution/release verification.


For maintainers or offline verification from a trusted checkout, install the .NET 10 SDK from `global.json` and the pinned official LikeC4 CLI (CI uses Node.js 22.23.3 and `likec4@1.59.4`):

```bash
dotnet tool restore
dotnet restore Repo2C4.slnx --locked-mode
dotnet build Repo2C4.slnx --configuration Release --no-restore
npm install --global likec4@1.59.4
bash scripts/verify-distribution.sh artifacts/distribution 1.0.0
```

The script packs **CLI, MCP and Agent**, verifies version/package identity, builds an isolated local NuGet feed and installs all three commands into separate temporary tool paths using a NuGet configuration with `<clear/>` package sources. It exercises a real `inspect → generate --apply → validate` workflow, checks the MCP stdio protocol, and runs the packaged Agent against the checked-in `library-only` fixture using a controlled loopback Ollama-compatible fake plus the installed real MCP. The fake deliberately emits no proposal, so the expected Agent outcome is the controlled `insufficient_evidence` terminal reason with no write. It never pushes a package, creates a tag or invokes a cloud API. Its temporary tool directories are deleted on completion. The three packages remain in ignored `artifacts/distribution/` for optional local use.

To install from previously verified package files (without using external feeds), create a NuGet.Config containing only the directory of the three local `.nupkg` files, then run:

```bash
dotnet tool install --tool-path ./local-tools/cli Repo2C4.Cli --version 1.0.0 --configfile ./NuGet.Config
dotnet tool install --tool-path ./local-tools/mcp Repo2C4.Mcp --version 1.0.0 --configfile ./NuGet.Config
dotnet tool install --tool-path ./local-tools/agent Repo2C4.Agent --version 1.0.0 --configfile ./NuGet.Config
./local-tools/cli/repo2c4 --help
./local-tools/mcp/repo2c4-mcp --help
./local-tools/agent/repo2c4-agent --help
```

Tool command extensions differ on Windows (`.exe`). The install script is a Linux/CI smoke test; Windows users can run the shown `dotnet tool install` commands with Windows-appropriate paths. Packages attached to the matching GitHub Release are the validated payload sent to NuGet.org and include SHA-256 checksums in `SHA256SUMS`. Prefer the exact version shown by the release notes rather than an unbounded latest install.

## Local repository → evidence → reviewed architecture → LikeC4

Inspect a local, **explicitly authorized** .NET repository into a new snapshot file:

```bash
repo2c4 inspect --repository /absolute/path/to/dotnet-repository --output ./snapshot.json
```

For a reproducible example from the Repo2C4 checkout use `examples/fixtures/library-only` and compare its snapshot with `examples/end-to-end/snapshot.v1.json`. A human reviewer uses the source evidence and original snapshot to produce a versioned `ArchitectureModel`; the checked-in `examples/end-to-end/architecture.c1.v1.json` and `architecture.c2.v1.json` are examples of deliberate review, **not** automatic inference. For optional local inference, configure an installed Ollama model and run `repo2c4 infer --snapshot snapshot.json --provider ollama --model-id YOUR_MODEL_ID --output candidate.json`. For hosted inference, provide a securely stored `OPENAI_API_KEY` and add `--provider openai --model-id YOUR_MODEL_ID --allow-external-ai`: only bounded, sanitized metadata leaves the host after explicit consent. OpenAI can incur charges; see [cloud privacy and cost](inference-openai.md). MCP model selection belongs to the MCP client and does not require credentials in this server.

**Never treat `candidate.json` as verified architectural truth.** Inspect its element/relation evidence IDs against the original snapshot and source, remove unsupported assertions, and retain `requiresReview` for any unverified relationships, actors, deployment or container boundaries. Save the reviewed result as `architecture.reviewed.json`; then:

```bash
repo2c4 generate --model architecture.reviewed.json --output ./likec4
repo2c4 generate --model architecture.reviewed.json --output ./likec4 --apply
repo2c4 validate --output ./likec4
```

Generation previews by default, `--apply` writes only controlled output and does not overwrite manually edited files. Review `likec4/evidence-report.md` for hypotheses/provenance. C1/C2 are supported by default; generate **C3 for one selected existing C2 container only** with `--c3-container CONTAINER_ID` using a previously reviewed C2 model (see [selective C3 example](../examples/end-to-end/README.md)). A library-only inventory is not proof of a running container. LikeC4 must be installed for `validate`, and its DSL checks are not a substitute for review of architecture claims.

## MCP client configuration and local filesystem policy

The MCP server is a separate stdio tool. The host must provide exactly one authorized, **absolute** directory; the server refuses missing roots, symlink/junction roots and traversal outside the root. Generated files must remain in destinations explicitly permitted by its tool policies. Example generic MCP client configuration (replace the tool path and authorized repository path):

```json
{
  "mcpServers": {
    "repo2c4": {
      "command": "/absolute/path/to/local-tools/mcp/repo2c4-mcp",
      "args": ["--repository-root", "/absolute/path/to/authorized-dotnet-repository"]
    }
  }
}
```

MCP uses protocol messages on stdout and diagnostics on stderr; do not pipe banners or logs to its stdout. The client obtains bounded evidence through `inspect_repository`, `get_evidence` and `get_snapshot`, proposes its own reviewed C1/C2 model, and uses `generate_likec4`/ `validate_likec4` with explicit write authorization. `repo2c4-agent` is one such MCP client, with Microsoft Agent Framework workflow/HITL policy; MCP remains independently usable by other hosts. See the [Agent guide](agent.md). See [MCP client guide](mcp-client.md) and [MCP access policy](mcp.md) for the evidence-first prompt, request limits, paginated evidence, review/write semantics and local root constraints. Never point an MCP server at an untrusted writable checkout while another process can swap symlinks.

## Versioned release: verification first, publication separately authorized

[Verify and optionally release Repo2C4 tools](../.github/workflows/release.yml) runs **only via workflow_dispatch on trusted `main`**. It requires a SemVer input matching the version already declared in MSBuild, runs restore/format/build/test, pins and installs official LikeC4, packs the three tools and tests isolated installation/execution. A default dry-run creates **no tag, GitHub Release, NuGet publication or public artifact**. The release job is skipped by default. To publish deliberately, select `publish_release=true` **and** enter `publication_confirmation=PUBLISH`. The write-scoped job runs inside the protected `release` environment, rechecks that `main` still points at the validated commit, validates the exact four release assets, creates the versioned GitHub Release as a draft, then authenticates to NuGet.org with Trusted Publishing. `NuGet/login` exchanges the GitHub Actions OIDC token for a short-lived `NUGET_API_KEY`; no long-lived NuGet API key is stored as a repository or environment secret. The NuGet.org Trusted Publishing policy must authorize repository `rodri-oliveira-dev/Repo2C4`, workflow `release.yml` and the protected `release` environment. Only `Repo2C4.Cli`, `Repo2C4.Mcp` and `Repo2C4.Agent` are pushed, and the GitHub Release is made public only after those pushes succeed. A final read-only consumer job installs that exact version from NuGet.org and executes all three public commands. After that succeeds, a separate OIDC-scoped job publishes the matching `Repo2C4.Mcp` metadata to the Official MCP Registry and verifies the exact version through the registry API. Failed publication or propagation is reported without printing the API key. NuGet publication is not transactional across the three package IDs: if a later push fails after an earlier package is accepted, rerun only from the same protected `main` commit and the exact validated payload. The workflow's hash verification treats an already-published package with the same SHA-256 as satisfied and rejects a same-version package with different bytes; never rebuild or replace one package of a partially published version.

The regular [CI](../.github/workflows/ci.yml) performs the same pack/clean-install smoke on push and PR without any AI key or release write permission. [CodeQL](../.github/workflows/codeql.yml), [Dependency Review](../.github/workflows/dependency-review.yml) and security/audit checks must remain green before the Phase 5 PR is merged. The dedicated [documentation PR workflow](architecture-pr.md) is a separate manual process, not release publication.

## Known limitations

Repo2C4 primarily inspects local .NET repositories and does not support arbitrary languages. It can optionally acquire public Git repositories over HTTPS through the CLI and, when explicitly enabled by the MCP host, through MCP; private/authenticated repositories remain unsupported. The source scanner does not execute repository code; it limits supported files, sizes, evidence counts and paths. C1/C2 and selected C3 are **reviewable proposals**; repository-static evidence does not independently prove runtime topology. Models and evidence reports may reveal repository-relative names and should be handled as potentially confidential. Cloud inference is opt-in and sends only sanitized but potentially revealing architecture metadata; local Ollama stays on loopback. The tool does not install LikeC4, render its own diagrams, auto-approve architecture, merge PRs or provide a SaaS service. Public release remains manually triggered and separately protected; ordinary pushes and pull requests never publish packages.
