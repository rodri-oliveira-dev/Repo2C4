# Contributing to Repo2C4

Follow the [roadmap](https://github.com/rodri-oliveira-dev/Repo2C4/issues/4), [Code of Conduct](CODE_OF_CONDUCT.md), and [security policy](SECURITY.md).

## Local verification

Install .NET 10 (see `global.json`) and Git. From the repository root:

```bash
dotnet tool restore
dotnet restore Repo2C4.slnx --locked-mode
dotnet format Repo2C4.slnx --verify-no-changes --no-restore
dotnet build Repo2C4.slnx --configuration Release --no-restore
dotnet test Repo2C4.slnx --configuration Release --no-build
```

Centralize NuGet versions in `Directory.Packages.props`. When changing packages or project references, regenerate SDK-owned lockfiles with `dotnet restore Repo2C4.slnx --force-evaluate`, review the diff and commit the results; never hand-edit lockfiles.

The Core project must not reference CLI or MCP; both executables may reference Core but not each other. Do not add functional MCP transport or AI integration in phase 1. Implement issues #5–#8 in sequence on `phase/01-foundation`, with one final phase PR. No NuGet, tag or GitHub Release publication is permitted until phase 5.

Write deterministic tests for observable behavior, update docs and the `Unreleased` changelog for meaningful changes, preserve pinned Actions and least-privilege permissions, and never commit secrets.
