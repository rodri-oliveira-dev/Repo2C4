# Semantic C3 v1.1.0 baseline

This note freezes the quality baseline used to start Phase 9 without changing the stable C1/C2 v1 contract.

## Reproducible fixture

The repository-local fixture
`tests/fixtures/semantic-c3/dotnet-observability-lab.ingestion.v1.1.c2.json`
mirrors the architectural shape observed while dogfooding `Ingestion.Api` in
`dotnet-observability-lab`. It is intentionally synthetic and bounded so the
test suite does not depend on another repository or execute application code.

Run only the baseline test with:

```bash
dotnet test tests/Repo2C4.Core.Tests/Repo2C4.Core.Tests.csproj \
  --filter FullyQualifiedName~SemanticC3BaselineTests
```

## v1.1.0 behavior

For the selected `el_ingestion_api` container, the current
`ArchitectureC3Builder` groups evidence into these generic proposals:

```text
HTTP interface
Integration adapter
Application dependency
```

The baseline also preserves evidence-backed relations from the generic
integration adapter to the existing PostgreSQL and Redis C1/C2 elements.
All proposals remain `requiresReview`, and repeated generation must keep the
same component and relation IDs.

## Semantic target

Phase 9 is expected to improve architectural responsibility resolution toward
an equivalent structure such as:

```text
HTTP ingestion endpoint
        |
        v
Idempotent ingestion use case
        |
        v
Transactional persistence
        |
        +--> PostgreSQL
        +--> Redis
```

Textual equality with a manually maintained C3 is not required. Improvement is
demonstrated when the generated proposal has equivalent architectural
responsibilities, every promoted component/relation has provenance, ambiguous
boundaries remain reviewable, and no unsupported component is fabricated.

This baseline deliberately does not implement semantic detection. It records
the starting point so issues #63-#69 can prove material improvement against a
stable reference.
