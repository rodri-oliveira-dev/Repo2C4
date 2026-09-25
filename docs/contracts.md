# Repo2C4 v1: evidence and architecture model contracts

The `Repo2C4.Core.Contracts` types are independent of the CLI, MCP transport and AI SDKs. The **source of truth is an evidence-bearing snapshot**, not free-form model output. A C4 element/relation is a confirmed assertion only when it cites existing evidence. Evidence references demonstrate provenance, not that a proposed architectural interpretation is automatically true.

## Schema and ownership

Both `RepositorySnapshot` and `ArchitectureModel` carry `schemaVersion: "1.0"`. Models embed the exact snapshot from which their evidence references originate. A snapshot contains `repositoryId` (a stable logical identifier, not an absolute filesystem path), `files`, `evidence` and `diagnostics`. Files contain only repository-relative paths, byte counts and an optional lowercase SHA-256 hash, **never file bodies**. Evidence provides an ID, factual `category`, normalized `relativePath`, optional 1-based `line`, `sourceType` and concise objective `description`. Diagnostics report bounded-scan omissions/errors without embedding repository content. Do not place secrets, absolute local paths or untrusted raw source text in descriptions, names or diagnostic messages.

`ArchitectureModel` defines `level` as `C1` or `C2`, `elements` and `relations`. Element `kind` is `actor`, `softwareSystem` or `container`; a project file does not by itself prove that the project is a C2 container. C1 disallows containers; at C2, each container must belong to an existing software system through `parentId`. Actors and systems are roots. Cyclic containment, missing/invalid parents, relation endpoints outside the model and duplicate IDs are rejected. A relationship is directed from `sourceId` to `destinationId`.

Each element and relation has `evidenceIds`, `status` (`confirmed` or `requiresReview`) and an optional `reviewReason`. A `confirmed` assertion requires at least one ID present in the embedded snapshot; an unsupported inference must use `requiresReview` with an explicit reason, even if supporting files exist but do not establish the inference. Referenced evidence must belong to an inventoried file. No automatic promotion from `requiresReview` to `confirmed` is permitted.

## Minimal snapshot example

```json
{
  "schemaVersion": "1.0",
  "repositoryId": "sample_repo",
  "files": [
    { "relativePath": "Sample.slnx", "sizeBytes": 42, "sha256": null }
  ],
  "evidence": [
    {
      "id": "ev_solution",
      "category": "solution",
      "relativePath": "Sample.slnx",
      "line": 1,
      "sourceType": "manifest",
      "description": "Solution includes the Sample project."
    }
  ],
  "diagnostics": []
}
```

## Minimal C1 model example

The model wraps the same snapshot; each `evidenceIds` value points into `snapshot.evidence`.

```json
{
  "schemaVersion": "1.0",
  "level": "C1",
  "snapshot": {
    "schemaVersion": "1.0",
    "repositoryId": "sample_repo",
    "files": [{ "relativePath": "Sample.slnx", "sizeBytes": 42 }],
    "evidence": [
      {
        "id": "ev_solution",
        "category": "solution",
        "relativePath": "Sample.slnx",
        "line": 1,
        "sourceType": "manifest",
        "description": "Solution includes the Sample project."
      }
    ],
    "diagnostics": []
  },
  "elements": [
    {
      "id": "el_sample",
      "kind": "softwareSystem",
      "name": "Sample system",
      "evidenceIds": ["ev_solution"],
      "status": "confirmed"
    }
  ],
  "relations": []
}
```

A proposed, unobserved relationship instead uses `"evidenceIds": []`, `"status": "requiresReview"` and a substantive `"reviewReason"`; do not invent the endpoint, actor or relation as an observed fact.

## Determinism and validation

Use `StableIds.ForEvidence(category, relativePath, line, objectiveDescription)`, `StableIds.ForElement(kind, stableLogicalKey)` and `StableIds.ForRelation(sourceId, destinationId, stableLogicalKey)` to generate deterministic lowercase ASCII IDs. Identity inputs must be stable across scans; for relationships use an enduring logical relation key rather than enumeration order. The helpers encode length-prefixed identity fields before SHA-256 and expose only a 24-character lowercase digest fragment. IDs must be unique in their respective collection, and repository-relative paths must use `/` without absolute prefixes, traversal, empty segments, drives or backslashes.

`ContractValidator.ValidateSnapshot/ValidateModel` return errors with machine-readable `Code`, JSON-like `Path` and an actionable, non-secret-bearing `Message`. `ContractJson.SerializeSnapshot/SerializeModel` validate before emission. `DeserializeSnapshot/DeserializeModel` validate after parsing, reject unsupported schema versions and normalize collection order. Files are sorted by relative path, evidence/elements/relations by ID, evidence references by ID and diagnostics by code/path/message, using ordinal comparison. Reordering source collections cannot change the emitted JSON. Null optional fields are omitted. All serializations use UTF-8-compatible JSON with stable camelCase property names; no source content is serialized.

## Compatibility policy

Version `1.0` is the only accepted version in the foundation phase. Unknown fields are ignored when reading v1 to accommodate additive producer metadata, but must never weaken required-field, provenance or semantic checks. A future backward-compatible v1 addition must be optional and preserve the meaning of existing fields; use a new major schema version for incompatible changes. Unknown major/minor version strings are rejected until explicitly supported. Do not silently transform a future schema into v1 or strip a `requiresReview` flag.

## v1 .NET evidence extraction categories

`RepositoryFactExtractor.Extract(options, cancellationToken)` returns a valid v1 snapshot that retains accepted file metadata and scan diagnostics while adding factual evidence. It emits no `ArchitectureElement` or `ArchitectureRelation`.

| Category | Observation and proof boundary |
| --- | --- |
| `dotnet.solution.project` | A .sln/.slnx lists an inventoried project, not a deployed container. |
| `dotnet.project` | An MSBuild project manifest exists; no project evaluation is performed. |
| `dotnet.project.kind` | SDK/output type/test flag indicates executable/library/test; static declarations may be conditional. |
| `dotnet.project.targetFramework` | A literal declared target framework; properties/imports are not evaluated. |
| `dotnet.project.reference` | A declared, inventoried build-time `ProjectReference`; not runtime communication. |
| `dotnet.project.testCandidate` | Test-framework package reference; does not independently establish a test executable. |
| `dotnet.runtime.http.candidate`, `dotnet.runtime.worker.candidate` | Recognizable Web/Worker SDK or source APIs; the host may never run. |
| `dotnet.integration.postgresql.candidate`, `dotnet.integration.rabbitmq.candidate`, `dotnet.integration.redis.candidate` | Identifiable package/source mentions; never sufficient to confirm HTTP, broker or database communication. |
| `deployment.docker.manifest` | Dockerfile or compose manifest presence, not a deployment. |

Every item retains relative path, optional 1-based line and a fixed description without source content. Unsupported model relations must remain `requiresReview` until separately verified. See [local fixtures and illustrative JSON](../examples/README.md).
