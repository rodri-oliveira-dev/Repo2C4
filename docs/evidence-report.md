# Evidence report and architectural review

Issue #17 adds a deterministic `evidence-report.md` generated from a reviewed v1 `ArchitectureModel`.

The report is intentionally metadata-only. It contains architecture IDs, evidence IDs, repository-relative source paths and optional line numbers already present in the snapshot. It does not copy source bodies, configuration values, tokens, secrets, absolute local paths or arbitrary hyperlinks.

## CLI

Generate LikeC4 and the report together:

```bash
repo2c4 generate --model architecture.json --output DIR
```

The output directory contains `specification.c4`, `model.c4`, `views.c4` and `evidence-report.md`.

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
