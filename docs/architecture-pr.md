# Manual LikeC4 documentation PR workflow (Phase 5, issue #22)

The [Reviewable LikeC4 documentation PR](../.github/workflows/architecture-pr.yml) GitHub Actions workflow is **manual only** (`workflow_dispatch`). It does not run on pull requests, forks, tags, arbitrary branches, or untrusted repositories. It checks out the selected repository's **protected `main` commit** with persisted checkout credentials disabled; the repository input must equal the host repository and `source_ref` must be `main`. The workflow will not operate on an external repository URL or execute code from the selected inspection root. Only the Repo2C4 solution from trusted `main` is built/tested.

**The new workflow can be dispatched from the default branch only after the Phase 5 PR has been reviewed and merged.** Tests of its helper and LikeC4 fixture run in normal CI on the Phase 5 branch, without dispatching any publishing job.

## Inputs

Choose **Actions → Reviewable LikeC4 documentation PR → Run workflow**, select **main** and provide:

| Input | Purpose |
| --- | --- |
| `repository` | Exact `owner/repo` of this repository; other repositories and forks are rejected. |
| `source_ref` | `main` only; a trusted source checkout is pinned to the dispatch SHA. |
| `repository_root` | Repository-relative local .NET root for a fresh evidence inspection; `.` inspects the checkout. No absolute, parent-traversing or symlinked paths. |
| `mode` | `reviewed` for a committed, human-reviewed model or `infer` for a tentative, AI-proposed model. |
| `model_path` | Required only in reviewed mode: repository-relative JSON `ArchitectureModel` whose embedded snapshot **exactly matches a fresh inspection** of `repository_root`. |
| `provider`, `model_id` | In hosted inference mode, `provider=openai` and an explicitly selected model ID. The runner cannot rely on a locally installed Ollama service. Leave both empty in reviewed mode. |
| `allow_external_ai` | Must be explicitly enabled in infer mode. Never used in reviewed mode. |
| `output_id` | Lowercase slug of at most 40 ASCII characters. Generated files are confined to `docs/generated/<output_id>/`. |

The example checked into this repository uses `repository_root=examples/fixtures/library-only`, `mode=reviewed`, `model_path=examples/end-to-end/architecture.c1.v1.json` and `output_id=library-example`. For a real repository, commit an independently reviewed v1 model with a snapshot produced from the selected trusted `main` root first. Static package/project candidates are not automatically considered proven deployment or runtime relations.

## Hosted inference and confidentiality

For infer mode, create the repository secret `OPENAI_API_KEY` under **Settings → Secrets and variables → Actions → New repository secret**. The workflow loads this secret only in the opt-in inference step. Missing consent, provider/model, or key causes failure before any remote inference request. Only the bounded, sanitized snapshot metadata goes to the fixed HTTPS OpenAI Responses endpoint, subject to API charges and your organization's confidentiality policy. Original snapshot, candidate JSON and cloud credential remain within the read-only validation job and are deleted before artifact upload. The resulting `evidence-report.md` may contain repository-relative paths and review-required proposals: check the resulting PR before publishing the documentation.

See [OpenAI cloud consent and cost](inference-openai.md) and [local Ollama inference](inference.md). The MCP host remains independent of both providers and the regular CI needs no AI credentials.

## Validation, diff and permissions

The read-only `prepare` job performs locked restore, formatting check, Release build and tests, official LikeC4 CLI validation (pinned `1.59.4`) and protected managed generation. The optional inference candidate is **not** treated as architecturally reviewed: its elements/relations remain `requiresReview`. Unsupported v1 data, stale evidence snapshots, invalid DSL, malformed output, file conflicts or failed checks abort before publication.

Only generated `.c4` files, `evidence-report.md` and the managed-output manifest can enter the validated artifact. No candidate JSON, original snapshot, credential, raw repository contents or external files are uploaded. If the bounded generated-file diff is empty, the `publish` job is skipped and **no PR is created**.

The separate `publish` job is the only job granted `contents: write`, `pull-requests: write` and `actions: read` for its short-lived `GITHUB_TOKEN`. It verifies the validated artifact's identity and checksums, rejects unexpected files, checks that `main` has not moved, and proposes changes through a dedicated `bot/repo2c4-<output_id>` branch. It never pushes to `main`. A currently open PR for the same branch is reused rather than duplicated; an orphaned/colliding branch requires manual review rather than a force push. The PR summarizes changed paths, successful LikeC4 validation and links its `evidence-report.md`, explicitly requiring human architecture review. It never requests approval or performs merge.

**Repository settings required for PR publication:** The repository must permit GitHub Actions to create pull requests and permit the workflow token to write contents and pull requests, subject to branch rulesets. In **Settings → Actions → General → Workflow permissions**, enable the setting permitting Actions to create and approve pull requests where available. The workflow itself does not approve any PR. A policy that blocks writing or PR creation produces a diagnostic; do not grant a permanent personal access token merely to bypass policy. PRs opened with the default `GITHUB_TOKEN` may not trigger all downstream PR workflows automatically; trigger/review required checks according to repository rules before manually merging.

## Reproducible negative tests

Normal CI runs `tests/automation/test_architecture_pr.py` using fixture data and fake external tools. Cases cover no-op without PR, schema rejection, LikeC4 failure, missing cloud key, refusal without consent, untrusted repository/ref, mismatched model snapshot, blocked write permission, and a validated diff expected to produce exactly one review PR. It also executes the real CLI plus installed official LikeC4 against the checked-in fixture, without creating a remote branch or PR. The additional workflow schema gate runs pinned `actionlint`.

No workflow dispatch, cloud inference, token-backed branch push or actual automated documentation PR is executed as part of the CI fixture tests. Verify those host-specific permissions manually when enabling this workflow on the default branch.
