# Repo2C4 offline CLI

The Phase 2 CLI exposes three local commands. None of them calls an AI provider, opens a pull request, evaluates MSBuild or turns package/project candidates into confirmed runtime architecture.

## Commands

### Inspect

```bash
repo2c4 inspect --repository PATH --output snapshot.json
```

`inspect` inventories one explicitly selected local repository and extracts the bounded evidence supported by the Core. The output is canonical v1 JSON.

The repository ID is derived deterministically from the selected root directory name. It is a local stable label, not a globally unique repository identity.

The snapshot output file must not already exist. Choose another path if it does.

### Generate

```bash
repo2c4 generate --model architecture.json --output DIR
```

The input must be a valid v1 `ArchitectureModel` that was proposed or reviewed by a human. The command does not infer a C4 model from the snapshot.

Four files are produced inside the selected directory:

- `specification.c4`
- `model.c4`
- `views.c4`
- `evidence-report.md`

Existing generated files are not replaced by default. To replace the four fixed outputs explicitly:

```bash
repo2c4 generate --model architecture.json --output DIR --overwrite
```

`evidence-report.md` maps model assertions to evidence IDs and repository-relative locations, lists hypotheses, scan warnings and missing origins, and omits source bodies and sensitive values. See [evidence report and architectural review](evidence-report.md).

To request C3 for exactly one reviewed C2 container, add `--c3-container ID`:

```bash
repo2c4 generate --model architecture.c2.json --output DIR --c3-container el_web
```

Without this option no C3 files are produced. A valid selection nests reviewed component proposals inside the selected container in `model.c4` and adds `c3.views.c4`; the remaining C2 containers do not receive component views automatically. The C3 proposal is bounded, keeps candidate/static signals under review, and fails when the selected container has insufficient evidence.

The output directory and any existing target file must not be a symlink, junction or reparse point. Generated filenames are fixed by Repo2C4 and cannot be supplied by model content.

### Validate

```bash
repo2c4 validate --output DIR
```

`validate` invokes the installed official LikeC4 CLI using the controlled command `likec4 validate`. Repo2C4 does not install Node.js or LikeC4 at runtime. The CI baseline uses LikeC4 `1.59.4`; see [LikeC4 CLI validation](likec4-validation.md).

## Exit codes

| Code | Meaning |
| ---: | --- |
| `0` | Command completed successfully. |
| `2` | Incorrect command or arguments. |
| `3` | Invalid v1 snapshot/model data. |
| `4` | LikeC4 validation failed or the configured LikeC4 CLI could not validate. |
| `5` | Local filesystem/path operation failed or overwrite policy blocked the operation. |

The underlying LikeC4 exit code is reported as diagnostic context but is mapped to Repo2C4 exit code `4`.

## Inference boundary

The supported flow is intentionally split:

```text
local repository
    -> inspect
    -> evidence snapshot
    -> human proposal/review
    -> ArchitectureModel
    -> generate
    -> LikeC4 files
    -> validate
```

Repo2C4 does not automatically convert every .NET project to a C4 container. Build-time references and package/runtime candidates remain evidence or hypotheses until explicitly reviewed in the model.

## Reproducible example

See [the checked-in end-to-end example](../examples/end-to-end/README.md). It uses the library-only fixture to demonstrate that the absence of an executable remains visible: the reviewed C2 model contains no fabricated container.
