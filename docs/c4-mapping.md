# C1/C2 mapping policy

Repo2C4 maps repository evidence into a reviewable `ArchitectureModel` without treating repository structure as deployed architecture. The evidence contract in `docs/contracts.md` remains the source of truth. These rules define how evidence may be proposed for C1/C2; they do not add a second architecture model and they do not authorize automatic promotion of hypotheses to confirmed facts.

## Core rule

A repository fact and an architectural assertion are different things.

- `Evidence` records what was observed in an inventoried file.
- `ArchitectureElement` and `ArchitectureRelation` record an architectural interpretation.
- `confirmed` means a reviewer accepted the interpretation and the assertion cites relevant evidence.
- `requiresReview` means the interpretation is still a proposal. It must explain why review is required.
- Evidence references establish provenance. They do not make a candidate category semantically true by themselves.

The mapper or reviewer must never invent actors, protocols, external systems, deployment boundaries, ownership boundaries or runtime calls merely to make a diagram complete.

## Precedence

Apply the following precedence when preparing a model:

1. **Explicit reviewed decision.** A reviewer may select the focal software system, container boundaries and relation wording, but a confirmed assertion must still cite evidence that supports that assertion.
2. **Direct repository evidence.** Static declarations can support the existence of a project, executable candidate, manifest or documented dependency. They do not automatically establish deployment/runtime semantics.
3. **Candidate evidence.** Categories ending in `.candidate` are leads for review only.
4. **Build-time dependency.** `dotnet.project.reference` is a build dependency, not runtime communication.
5. **Absence of evidence.** Omit the assertion, or include it only as `requiresReview` with an explicit reason. Never mark it confirmed.

When evidence conflicts, prefer the narrower factual interpretation and keep the architectural assertion under review.

## C1 rules

C1 describes the system of interest and its context.

- Choosing the focal software-system boundary is an external architectural decision. A solution or repository name may suggest a name, but it does not prove the boundary.
- C1 contains only `softwareSystem` and `actor` elements. Containers are invalid at C1 and are rejected by the v1 validator.
- Do not create an actor from endpoint names, authentication packages, test users or naming conventions.
- Do not create an external system only because a client package or technology name is present.
- A proposed actor or external system with insufficient evidence may be represented as `requiresReview`; it must not be presented as observed fact.
- Relation descriptions should describe business/technical interaction only to the level supported by evidence. Protocol names such as HTTP, AMQP or gRPC require evidence of that protocol.

### Positive example

A repository contains explicit documentation or configuration that identifies an external system and a reviewed model cites that evidence. The external system and its relation can be confirmed after review.

### Negative example

`Npgsql`, `RabbitMQ.Client`, `StackExchange.Redis`, an HTTP SDK or a `ProjectReference` does not justify creating C1 systems or confirmed runtime relations by itself.

## C2 rules

C2 refines one selected software system into containers.

- An executable/Web/Worker project can be a **container candidate**.
- A common library, test project or `ProjectReference` target is not a container by default.
- A project-per-container rule is forbidden. Multiple executable projects may belong to one container, or one repository may contain code for multiple systems; the repository alone does not decide this.
- `dotnet.runtime.http.candidate` and `dotnet.runtime.worker.candidate` support candidacy, not a confirmed deployment boundary.
- PostgreSQL, RabbitMQ and Redis candidate evidence identifies possible technologies. It does not prove that the database/broker/cache is owned by the focal system, deployed as a C2 container, or contacted at runtime.
- Docker/compose manifest presence is evidence that a manifest exists, not proof that an image or service is deployed.
- Infrastructure may be modeled as a C2 container only after the ownership/deployment boundary is reviewed.
- A relation based only on `.candidate` evidence or `dotnet.project.reference` remains `requiresReview`.

## Relation semantics

The v1 relation contract has source, destination, description, evidence references and review status. It intentionally has no separate protocol/type field.

For v1:

- use the relation `description` for the reviewed interaction semantics;
- keep any proposed protocol outside the confirmed wording until evidence supports it;
- use a stable logical relation key when generating the relation ID;
- never treat package names as a relation type.

A relation whose endpoint itself is a hypothesis must also remain under review.

## Metadata for external decisions

The final `ArchitectureModel` stays the only serialized architecture contract. A reviewer, UI, future AI adapter or MCP client may keep the following decision metadata while preparing it:

| Decision metadata | Purpose | v1 serialization |
| --- | --- | --- |
| focal-system stable key | Identifies the selected system boundary independently of display name | used to derive the element ID |
| proposed name | Human-facing candidate name | `ArchitectureElement.name` after review |
| proposed description | Rationale/context for the reviewer | review-side metadata; v1 has no element-description field |
| candidate kind | software system, actor or container proposal | `ArchitectureElement.kind` after review |
| evidence IDs | Provenance used by the decision | `evidenceIds` |
| include/exclude decision | Prevents every discovered project from becoming an element | represented by presence/absence in the model |
| parent decision | Assigns a reviewed container to the focal system | `parentId` |
| relation wording | Reviewed interaction semantics | `ArchitectureRelation.description` |
| protocol proposal | Optional reviewer metadata, only confirmed when evidenced | do not serialize as fact unless supported |
| review status/reason | Separates accepted assertions from hypotheses | `status` / `reviewReason` |

Do not create a parallel serialized schema for these fields in Phase 2. If an additive contract field becomes necessary later, evolve v1 deliberately and preserve compatibility.

## Partial models and diagnostics

A partial model is valid and preferable to a fabricated complete model.

For a library-only repository:

- the library project is not converted into a container;
- no actor or runtime relation is created from absence of context;
- the focal software-system proposal may remain `requiresReview`;
- the unresolved boundary is explained in `reviewReason`.

`RepositorySnapshot.diagnostics` remains reserved for inventory/extraction diagnostics. Mapping-specific uncertainty must not be injected into the snapshot merely to obtain a diagnostic code. In v1, the unresolved mapping condition is carried by the relevant `reviewReason` and by omission of unsupported elements/relations.

## Checked-in fixtures

The Phase 2 fixtures under `examples/models/` are intentionally review-oriented:

- `acme.c1.v1.json` shows a C1 proposal with a focal system and an unsupported external-system proposal, no fabricated actor and no invented protocol.
- `acme.c2.v1.json` shows Web/Worker/PostgreSQL/RabbitMQ candidates while keeping deployment and runtime relations under review; the shared library is not a container.
- `library-only.c2.partial.v1.json` is a valid partial C2 model with no containers or relations and an explicit review reason explaining the missing executable boundary.

All fixture evidence IDs resolve inside the embedded v1 snapshot. Tests deserialize them through `ContractJson`, validate the contract, and assert the negative rules above.

## Out of scope

This policy does not implement LikeC4 emission, LikeC4 validation, CLI commands, MCP transport, AI calls, C3, external services or automatic PR generation.
