# Changelog

Notable changes to Repo2C4 follow [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- Multi-project foundation with isolated Core, CLI and MCP hosts and corresponding test projects.
- Explicit CLI/MCP startup behavior, MCP stdout isolation and CI smoke tests.
- Bounded local repository scanner with normalized relative paths, mandatory sensitive-file exclusions, symlink rejection, file/byte/entry budgets, cancellation and counted omission diagnostics.
- Evidence-backed .NET solution/project/source extraction with safe bounded reads, runtime integration candidates, XML safety checks and reproducible fixtures.
- C1/C2 evidence-to-model mapping policy with review-oriented v1 fixtures and negative tests that prevent candidate evidence, build-time references and common libraries from becoming confirmed runtime architecture.
- Pure deterministic LikeC4 C1/C2 emitter producing `specification.c4`, `model.c4` and `views.c4` in memory, with review/provenance metadata, DSL-safe escaping and golden fixtures.

### Changed
- Replaced inherited single-library packaging baseline with a non-packable product scaffold.
- Disabled inherited template release and NuGet publication until phase 5.
