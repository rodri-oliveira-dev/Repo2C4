# LikeC4 CLI validation

Repo2C4 validates generated LikeC4 sources through the official `likec4` CLI. The runtime adapter does **not** install Node.js or LikeC4. Installation remains an explicit responsibility of the host/operator.

## Pinned integration baseline

The Phase 2 CI baseline is pinned to:

- Node.js `22.23.3`
- LikeC4 `1.59.4`

CI installs the exact LikeC4 package version with:

```bash
npm install --global --no-audit --no-fund likec4@1.59.4
```

and validates a workspace with:

```bash
likec4 validate
```

The official command recursively reads the LikeC4 workspace from the current working directory and exits non-zero when validation errors are present.

## Core adapter

`LikeC4CliValidator.ValidateAsync(workspacePath)` runs only the controlled command:

```text
likec4 validate
```

The workspace path is used only as the child process working directory. Repository/model content cannot add command-line arguments, replace the executable or inject shell syntax. `UseShellExecute` is disabled and arguments are added through `ProcessStartInfo.ArgumentList`.

Hosts may explicitly provide a different executable path through `LikeC4CliValidationOptions`, for example when the pinned binary is installed in a controlled tool directory. The executable choice belongs to the host configuration, not to the repository being inspected.

## Failure model

Validation returns `LikeC4ValidationResult` instead of treating every external-tool failure as a valid/invalid boolean.

Reserved adapter exit codes:

| Exit code | Meaning |
| ---: | --- |
| `0` | LikeC4 validation completed successfully. |
| `2` | Workspace path is invalid or missing before process start. |
| `124` | Validation exceeded the configured timeout and the process tree was terminated. |
| `127` | The configured LikeC4 executable could not be started. |
| other non-zero | Exit code returned by the LikeC4 CLI. |

`IsValid` is true only when the official process exits with code `0`.

## Diagnostic safety

The validator captures stdout/stderr only to identify affected `.c4`/`.likec4` file paths. It does not return raw CLI output, source lines, quoted values or exception text from the external process.

Returned diagnostics contain:

- a stable code such as `likec4.validationError`, `likec4.cliUnavailable` or `likec4.timeout`;
- an optional normalized source-file path;
- a fixed actionable message.

This deliberately trades detail for safety. A developer who needs the full parser diagnostic can run `likec4 validate` locally inside the generated workspace.

Captured process output is bounded independently for stdout and stderr before path extraction.

## Tests

Regular Core tests require only .NET. They cover controlled failures such as a missing workspace and an unavailable CLI without requiring Node.js or network access.

The real LikeC4 integration test is activated in CI with:

```text
REPO2C4_LIKEC4_INTEGRATION=1
```

That integration test verifies:

1. checked-in C1 and C2 golden workspaces emitted by issue #10 validate successfully with LikeC4 `1.59.4`;
2. a syntax error produces a controlled non-zero failure;
3. a missing element reference produces a controlled non-zero failure;
4. a sentinel embedded in invalid source text is not returned in Repo2C4 diagnostics.

The library-only partial fixture remains covered by deterministic emitter tests. LikeC4 validation does not transform partial architecture into additional elements or relations.

## Boundaries

This adapter performs no AI calls, repository inspection, package installation, shell command construction, PR creation or file generation. Writing generated files is owned by the CLI/MCP host layer. The offline CLI wiring is introduced by issue #12.
