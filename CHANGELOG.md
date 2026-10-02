# Changelog

Notable changes to Repo2C4 follow [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- Reviewable Semantic C3 HTTP/application proposals that aggregate routes by semantic boundary and distinguish DI-/handler-backed application services from name-only classes.
- Bounded, cancellation-aware C# structural fact extraction for Semantic C3, covering symbols, DI/wiring, Minimal API/controllers, hosted services, DbContext, messaging role signals and conservative intra-project collaboration facts without build/code execution or source/literal propagation.
- Additive, versioned Semantic C3 contract foundation with deterministic component taxonomy, stable source-symbol identity, reviewable internal/external relations, conceptual multi-container selection, and a reproducible v1.1.0 dogfooding baseline.

## [1.1.0] - 2026-10-02

### Added
- Reproducible external-integration end-to-end coverage based on the official DotNetRepoInspector `v1.6.5` schema `1.6` shape, spanning HTTP, database, cache, storage, messaging publish/consume, conservative review semantics, real MCP transport, deterministic Agent execution, and official LikeC4 validation.
- Optional, security-bounded external integration report consumption across CLI `--integration-report`, MCP `inspect_repository.integrationReportPath`, and Agent host authorization, with one Core import/merge path, session-bound `external.*` evidence, controlled schema/path failures, explicit counts, confidence/direction guidance, and quickstart documentation.
- Versioned external-integration interoperability boundary, deterministic importer, and conservative C1/C2 mapping for DotNetRepoInspector `InspectionReport` schema `1.6+`, covering HTTP, data/cache/storage, and provider-backed messaging publish/consume with normalized evidence/provenance, repository correlation, explicit confidence/review policy, pending-evidence reporting, bounded untrusted-input limits, adversarial fixtures, and no runtime or NuGet dependency between the products' cores.

### Changed
- Successful external-integration imports now retain sanitized correlation/truncation diagnostics in the resulting snapshot, and unknown or nonsensical data-integration directions cannot produce confirmed relations.

## [1.0.1] - 2026-10-01

### Added
- Human-in-the-loop LikeC4 application using the Agent Framework `ApprovalRequiredAIFunction`: validated destination-specific C1/C2 previews show file/change summaries and `requiresReview` IDs, bind the local human decision to the framework approval request, recheck preview freshness/conflicts before any `write=true` MCP call and never auto-approve from repository/model content.
- Explicit Microsoft Agent Framework Workflow orchestration for Agent analysis with typed analysis/evidence-report/preview/validation executors, a finite configurable 1-3 validation-attempt graph, sanitized correction feedback, structured terminal results and dry-run-only MCP enforcement.
- Evidence-first Agent analysis entrypoint with explicit `--goal`, host-gated optional C3 selection, C1/C2 preview guidance, review-status rules, prompt-injection resistance and final fact/hypothesis/diagnostic summaries driven only through MCP tools.
- Local Repo2C4 MCP stdio integration for the Agent through the official C# MCP SDK, with required-tool/schema startup validation, direct MCP function-tool exposure to Agent Framework, a forced preview-only generation wrapper, explicit repository-root authorization, child environment isolation and lifecycle/cancellation coverage without an Agent-to-Core/MCP project reference.
- Configurable Repo2C4 Agent model providers through the shared `IChatClient` contract: local-loopback Ollama via OllamaSharp and explicit-consent OpenAI via Microsoft.Extensions.AI.OpenAI, with environment-only credentials, bounded timeouts/cancellation and sanitized provider failures.
- Foundational .NET 10 `Repo2C4.Agent` host built on Microsoft Agent Framework and `IChatClient`, with explicit provider/model configuration, versioned evidence-first instructions, testable agent/session seams, controlled diagnostics and no dependency on Core/CLI/MCP.
- Version-coherent CLI, MCP and Agent .NET tool packages (`repo2c4` / `repo2c4-mcp` / `repo2c4-agent`), isolated local-feed install/execute including a controlled local-model Agent smoke against a real MCP fixture, manual protected release with explicit publication opt-in, bilingual installation/Agent/HITL guidance and reproducible examples; Core stays non-packable.
- Manual, main-only reviewable LikeC4 documentation GitHub Actions workflow with trusted source/root checks, reviewed-model or explicit-consent cloud inference, locked build/test, official LikeC4 validation, managed-diff gates, bounded artifact handoff, dedicated minimum write permissions, duplicate-PR protection, credential-free fixture tests and human-review-only PR descriptions.
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
- Added consumer-facing NuGet metadata for the product packages: the Repo2C4 repository URL as CLI `PackageProjectUrl` and a shared package icon for CLI, MCP and Agent.
- Replaced inherited single-library packaging baseline with a non-packable product scaffold.
- Disabled inherited template release and NuGet publication until phase 5.
