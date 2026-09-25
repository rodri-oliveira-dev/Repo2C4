# Repo2C4 CLI

The offline commands `inspect`, `generate` and `validate` do not call an AI provider, open a pull request, evaluate MSBuild or turn package/project candidates into confirmed runtime architecture. The optional `infer` command proposes a review-required architecture using either local Ollama or explicitly authorized OpenAI cloud inference; it never generates C4 files.

## Commands

### Inspect

```bash
repo2c4 inspect --repository PATH --output snapshot.json
```

`inspect` inventories one explicitly selected local repository and extracts the bounded evidence supported by the Core. The output is canonical v1 JSON.

The repository ID is derived deterministically from the selected root directory name. It is a local stable label, not a globally unique repository identity.

The snapshot output file must not already exist. Choose another path if it does.

### Infer (optional, local or explicitly authorized cloud AI)

```bash
repo2c4 infer --snapshot snapshot.json --provider ollama --model-id IDENTIFIER --output candidate.json
```

For OpenAI cloud inference, add `--provider openai --allow-external-ai` and provide `OPENAI_API_KEY` through the host environment. It reports the number of anonymized files and sanitized evidence records before sending them to the fixed HTTPS Responses API. Without explicit consent or a key no cloud request is made. The selected model and current token-based API pricing determine cost; sanitized metadata still leaves your machine. See [cloud consent, pricing and confidentiality](inference-openai.md).\n\nThe Ollama command accepts `--endpoint http://127.0.0.1:11434/` and `--timeout-seconds 90`. Only loopback HTTP endpoints are supported. It transmits a bounded and sanitized snapshot projection, never raw repository content, secrets, original file paths or free-form descriptions. The provider returns a versioned model proposal; the CLI attaches the original local snapshot, validates the v1 contract and marks **every AI-generated assertion as requiring human review**. It refuses invalid JSON, malformed models, timeouts, unavailable local models and existing output files. See [local Ollama inference and the reviewed example](inference.md).

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

Generation is preview-only by default. Repo2C4 reports `added`, `modified`, `unchanged` or `conflict` for each managed output and does not modify the filesystem.

To apply a reviewed preview:

```bash
repo2c4 generate --model architecture.json --output DIR --apply
```

The first successful apply creates `.repo2c4-manifest.json`, which stores the model schema version and SHA-256 hash of every managed output. Later applies are permitted only when the current file still matches the manifest hash. Manually edited, missing previously-managed, symlinked, or unmanaged colliding files are reported as conflicts and are left untouched.

`evidence-report.md` maps model assertions to evidence IDs and repository-relative locations, lists hypotheses, scan warnings and missing origins, and omits source bodies and sensitive values. See [evidence report and architectural review](evidence-report.md).

To request C3 for exactly one reviewed C2 container, add `--c3-container ID`:

```bash
repo2c4 generate --model architecture.c2.json --output DIR --c3-container el_web
```

Without this option no C3 files are produced. A valid selection nests reviewed component proposals inside the selected container in `model.c4` and adds `c3.views.c4`; the remaining C2 containers do not receive component views automatically. The C3 proposal is bounded, keeps candidate/static signals under review, and fails when the selected container has insufficient evidence.

The output directory and managed files must remain inside the selected root and must not be symlink, junction or reparse points. Generated filenames are fixed by Repo2C4 and cannot be supplied by model content. Writes are prepared in a transaction directory and the manifest is replaced only after all output files have been prepared; a failed commit restores previously managed files on a best-effort basis and never deletes unknown files.

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
| `6` | Explicit local inference provider unavailable or timed out. |

The underlying LikeC4 exit code is reported as diagnostic context but is mapped to Repo2C4 exit code `4`.

## Inference boundary

The supported flow is intentionally split. The optional inference stage does not change the offline steps:

```text
local repository
    -> inspect
    -> evidence snapshot
    -> optional infer (local or explicitly authorized cloud) -> review-required candidate.json
    -> human proposal/review
    -> ArchitectureModel
    -> generate
    -> LikeC4 files
    -> validate
```

Repo2C4 does not automatically convert every .NET project to a C4 container. Build-time references and package/runtime candidates remain evidence or hypotheses until explicitly reviewed in the model.

## Reproducible example

See [the checked-in end-to-end example](../examples/end-to-end/README.md). It uses the library-only fixture to demonstrate that the absence of an executable remains visible: the reviewed C2 model contains no fabricated container.
