# Repo2C4

Repo2C4 is an evolving .NET 10 tool for collecting verifiable architectural evidence from local .NET repositories and, in later phases, generating reviewable LikeC4 documentation. Inference must not convert unsupported hypotheses into confirmed facts.

## Architecture and current scope

| Project | Responsibility |
| --- | --- |
| `src/Repo2C4.Core` | Reusable contracts and inspection logic (upcoming foundation issues). |
| `src/Repo2C4.Cli` | Offline CLI host; feature commands are not yet implemented. |
| `src/Repo2C4.Mcp` | Stdio-safe MCP host scaffold; protocol transport arrives in phase 3. |
| `tests/Repo2C4.*.Tests` | Separate boundary and startup tests for each product project. |

CLI and MCP reference Core, never each other. Core does not reference the hosts. There is no repository inspection, AI provider, renderer or live MCP protocol at this stage.

## Prerequisites and verification

Install the .NET 10 SDK selected in `global.json` and Git. From the repository root:

```bash
dotnet tool restore
dotnet restore Repo2C4.slnx --locked-mode
dotnet format Repo2C4.slnx --verify-no-changes --no-restore
dotnet build Repo2C4.slnx --configuration Release --no-restore
dotnet test Repo2C4.slnx --configuration Release --no-build
dotnet test Repo2C4.slnx --configuration Release --no-build --coverlet --coverlet-output-format cobertura
```

The baseline retains Central Package Management, committed SDK-generated package locks, analyzers, nullable checks, deterministic builds, warnings as errors and NuGet auditing.

## Entry point smoke tests

```bash
dotnet run --project src/Repo2C4.Cli/Repo2C4.Cli.csproj -- --help
dotnet run --project src/Repo2C4.Mcp/Repo2C4.Mcp.csproj -- --help
```

CLI help is written to stdout. MCP help and diagnostics are written **only to stderr** to reserve stdout for future MCP JSON-RPC traffic. Unsupported commands exit with code 2; a successful MCP handshake is never simulated. CI additionally validates both executable entrypoints with process-level smoke tests.

## CI and distribution

`.github/workflows/ci.yml` validates locked restore, formatting, Release build, tests, coverage and smoke tests. CodeQL, Dependency Review and optional SonarQube Cloud checks remain available; [Sonar setup](docs/sonarqube-cloud.md) requires `SONAR_TOKEN`.

**Publication is disabled through phase 4:** projects are non-packable, the template's release workflow is removed, and CI produces no NuGet package. Installation and release distribution are defined in phase 5.

See [roadmap #4](https://github.com/rodri-oliveira-dev/Repo2C4/issues/4). All foundation issues #5–#8 share branch `phase/01-foundation`, and a single PR is opened only when the phase is complete.
