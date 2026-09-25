# Changelog

Notable changes to Repo2C4 follow [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- Multi-project foundation with isolated Core, CLI and MCP hosts and corresponding test projects.
- Explicit CLI/MCP startup behavior, MCP stdout isolation and CI smoke tests.
- Bounded local repository scanner with normalized relative paths, mandatory sensitive-file exclusions, symlink rejection, file/byte/entry budgets, cancellation and counted omission diagnostics.
- Evidence-backed .NET solution/project/source extraction with safe bounded reads, runtime integration candidates, XML safety checks and reproducible fixtures.

### Changed
- Replaced inherited single-library packaging baseline with a non-packable product scaffold.
- Disabled inherited template release and NuGet publication until phase 5.
