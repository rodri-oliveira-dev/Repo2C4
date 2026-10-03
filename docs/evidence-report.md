[Português (Brasil)](evidence-report.pt-BR.md)

# Evidence report and architectural review

Issue #17 adds a deterministic `evidence-report.md` generated from a reviewed v1 `ArchitectureModel`.

The report is intentionally metadata-only. It contains architecture IDs, evidence IDs, repository-relative source paths and optional line numbers already present in the snapshot. It does not copy source bodies, configuration values, tokens, secrets, absolute local paths or arbitrary hyperlinks.

## CLI

Generate LikeC4 and the report together:

```bash
repo2c4 generate --model architecture.json --output DIR          # preview only
repo2c4 generate --model architecture.json --output DIR --apply  # write managed outputs
```

After `--apply`, the output directory contains `specification.c4`, `model.c4`, `views.c4`, `evidence-report.md` and `.repo2c4-manifest.json`.

## Review workflow

1. Inspect the repository and build or receive a reviewable `ArchitectureModel`.
2. Generate the report and inspect **Hypotheses requiring review**.
3. Follow the referenced evidence ID to its repository-relative path and optional line.
4. If evidence is insufficient, keep `requiresReview` and refine `reviewReason`.
5. If independent review confirms the assertion and supporting evidence exists, change it to `confirmed`.
6. Regenerate the report and validate LikeC4 before publication.

Repository-static or candidate signals are not upgraded to confirmed runtime relationships merely because they appear in the report.

## MCP

The read-only `get_evidence_report` tool accepts a session `snapshotId` and complete v1 model. The server verifies that the embedded snapshot matches the session snapshot and returns a bounded summary of the report: counts, review-required assertion IDs and warning codes. The Markdown body remains a CLI artifact; MCP does not return source bodies, configuration values, absolute paths, or write to the repository.


## Semantic C3 evidence report

When C3 is requested from a snapshot containing `semanticC3Facts`, generation additionally produces `semantic-c3-evidence-report.md`. It is separate from the C1/C2 report because C3 component provenance includes stable source-symbol facts as well as normal architecture evidence.

The Semantic C3 report lists component/relation IDs, responsibility categories, repository-relative locations, structural signal classes, missing confidence signals, review status and bounded graph diagnostics. It never includes source bodies, arbitrary literals, configuration values or absolute paths.

A `confirmed` internal C3 relation means the configured static confidence rule was satisfied (for example supported wiring plus a statically resolved collaboration). It is not runtime tracing. Component boundaries remain reviewable.

See [Semantic C3](semantic-c3.md).
