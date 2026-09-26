# Repo2C4 distribution and verified release (Phase 5)

Repo2C4 v1.0.0 ships two **separate .NET 10 tool packages**, `Repo2C4.Cli` (command `repo2c4`) and `Repo2C4.Mcp` (command `repo2c4-mcp`). The shared Core remains an internal project reference, not a separately published package. Package version comes from `Directory.Build.props`; the two products use the same version and distinct, non-placeholder package IDs. The tools target .NET 10, so a compatible .NET runtime/SDK must be installed. LikeC4 is an **independent, externally installed** validator and renderer, not bundled inside Repo2C4.

## Install the two tools without publishing

From a trusted checkout of the [Repo2C4 repository](https://github.com/rodri-oliveira-dev/Repo2C4), install the .NET 10 SDK from `global.json` and the pinned official LikeC4 CLI (CI uses Node.js 22.23.3 and `likec4@1.59.4`):

```bash
dotnet tool restore
dotnet restore Repo2C4.slnx --locked-mode
dotnet build Repo2C4.slnx --configuration Release --no-restore
npm install --global likec4@1.59.4
bash scripts/verify-distribution.sh artifacts/distribution 1.0.0
```

The script packs **only CLI and MCP**, verifies version and package identity, builds an isolated local NuGet feed and installs both commands into separate temporary tool paths using a NuGet configuration with `<clear/>` package sources. It exercises a real `inspect → generate --apply → validate` workflow and checks that MCP help writes **only to stderr** and an unauthorized/missing root is rejected. It never pushes a package, creates a tag or invokes a cloud API. Its temporary tool directories are deleted on completion. The two packages remain in ignored `artifacts/distribution/` for optional local use.

To install from previously verified package files (without using external feeds), create a NuGet.Config containing only the directory of the two local `.nupkg` files, then run:

```bash
dotnet tool install --tool-path ./local-tools/cli Repo2C4.Cli --version 1.0.0 --configfile ./NuGet.Config
dotnet tool install --tool-path ./local-tools/mcp Repo2C4.Mcp --version 1.0.0 --configfile ./NuGet.Config
./local-tools/cli/repo2c4 --help
./local-tools/mcp/repo2c4-mcp --help
```

Tool command extensions differ on Windows (`.exe`). The install script is a Linux/CI smoke test; Windows users can run the shown `dotnet tool install` commands with Windows-appropriate paths. Packages downloaded from a future explicit GitHub Release have SHA-256 checksums in the attached `SHA256SUMS`. **Do not assume the packages are already available on NuGet.org**: this phase does not publish there.

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

MCP uses protocol messages on stdout and diagnostics on stderr; do not pipe banners or logs to its stdout. The client obtains bounded evidence through `inspect_repository`, `get_evidence` and `get_snapshot`, proposes its own reviewed C1/C2 model, and uses `generate_likec4`/ `validate_likec4` with explicit write authorization. See [MCP client guide](mcp-client.md) and [MCP access policy](mcp.md) for the evidence-first prompt, request limits, paginated evidence, review/write semantics and local root constraints. Never point an MCP server at an untrusted writable checkout while another process can swap symlinks.

## Versioned release: verification first, publication separately authorized

[Verify and optionally release Repo2C4 tools](../.github/workflows/release.yml) runs **only via workflow_dispatch on trusted `main`**. It requires a SemVer input matching the version already declared in MSBuild, runs restore/format/build/test, pins and installs official LikeC4, packs the two tools and tests isolated installation/execution. A default dry-run creates **no tag, GitHub Release, NuGet publication or public artifact**. The release job is skipped by default. To publish GitHub assets deliberately, select `publish_github_release=true` **and** enter `publication_confirmation=PUBLISH`; only then does the separate write-scoped job recheck that `main` still points at the validated commit, reject an existing tag/release, verify package SHA-256 and create the version tag and GitHub Release with exactly the two product packages and `SHA256SUMS`. This option publishes GitHub Release assets, **not NuGet.org**. Do not request a public release before reviewing version, package contents and repository permissions.

The regular [CI](../.github/workflows/ci.yml) performs the same pack/clean-install smoke on push and PR without any AI key or release write permission. [CodeQL](../.github/workflows/codeql.yml), [Dependency Review](../.github/workflows/dependency-review.yml) and security/audit checks must remain green before the Phase 5 PR is merged. The dedicated [documentation PR workflow](architecture-pr.md) is a separate manual process, not release publication.

## Known limitations

Repo2C4 initially inspects local .NET repositories rather than arbitrary languages or remote Git URLs. The source scanner does not execute repository code; it limits supported files, sizes, evidence counts and paths. C1/C2 and selected C3 are **reviewable proposals**; repository-static evidence does not independently prove runtime topology. Models and evidence reports may reveal repository-relative names and should be handled as potentially confidential. Cloud inference is opt-in and sends only sanitized but potentially revealing architecture metadata; local Ollama stays on loopback. The tool does not install LikeC4, render its own diagrams, auto-approve architecture, merge PRs, publish NuGet.org or provide a SaaS service. Phase 6 addresses broader public adoption and officially available installation channels.
