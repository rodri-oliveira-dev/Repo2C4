# Changelog

Notable changes to Repo2C4 follow [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- Explicit-consent OpenAI Responses API inference adapter with caller-selected model, environment-only credentials, bounded sanitized upload, safe cloud HTTP diagnostics, fake-transport tests and bilingual cost/privacy documentation; Core and MCP remain provider-independent.
- Optional, local-only `infer` CLI command backed by a configurable Ollama model, sanitized and bounded evidence projection, mandatory human-review status, structured v1 candidate validation, guarded candidate file creation, fake-HTTP tests and bilingual walkthroughs.
- Multi-project foundation with isolated Core, CLI and MCP hosts and corresponding test projects.
- Explicit CLI/MCP startup behavior, MCP stdout isolation and CI smoke tests.
- Bounded local repository scanner with normalized relative paths, mandatory sensitive-file exclusions, symlink rejection, file/byte/entry budgets, cancellation and counted omission diagnostics.
- Evidence-backed .NET solution/project/source extraction with safe bounded reads, runtime integration candidates, XML safety checks and reproducible fixtures.
- C1/C2 evidence-to-model mapping policy with review-oriented v1 fixtures and negative tests that prevent candidate evidence, build-time references and common libraries from becoming confirmed runtime architecture.
- Pure deterministic LikeC4 C1/C2 emitter producing `specification.c4`, `model.c4` and `views.c4` in memory, with review/provenance metadata, DSL-safe escaping and golden fixtures.
- Controlled official LikeC4 CLI validation adapter with bounded diagnostics, timeout/unavailable-tool handling and pinned real CLI validation in CI.
- Offline CLI commands `inspect`, `generate` and `validate`, deterministic end-to-end fixtures, explicit overwrite protection and CI smoke coverage for the complete Phase 2 flow.
- Local MCP stdio server foundation with maintained SDK transport, explicit authorized-root validation, traversal/symlink rejection, bounded Phase 3 host policies, cancellation handling and process-level handshake/tool-listing coverage.
- Read-only MCP `inspect_repository`, `get_evidence` and `get_snapshot` tools reusing v1 Core evidence, with session-scoped expiring snapshots, authenticated bounded pagination, response ceilings and secret/prompt-injection negative coverage.
- MCP `generate_likec4` and `validate_likec4` tools bound to session snapshots, with dry-run-by-default generation, fabricated-evidence/review-boundary checks, protected no-overwrite writes and official LikeC4 validation for proposed or authorized workspaces.
- Vendor-neutral MCP client documentation, reusable evidence-first C1/C2 prompts and a deterministic protocol-client end-to-end test that compares preview/written LikeC4 with versioned golden files without any hosted AI dependency.

### Changed
- Replaced inherited single-library packaging baseline with a non-packable product scaffold.
- Disabled inherited template release and NuGet publication until phase 5.
