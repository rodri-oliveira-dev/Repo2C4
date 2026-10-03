[Português (Brasil)](c4-mapping.pt-BR.md)

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

## External integration evidence

Imported `external.*` evidence may add a root `softwareSystem` dependency only when the finding supplies a target. It never creates a child container, deployment node, owner, remote producer, or remote consumer. HTTP without a target remains only in the evidence report rather than receiving an invented endpoint.

The caller explicitly selects the focal root software system; the mapper never chooses it by name or collection order. For C2, project-to-origin correlation first uses project-file provenance already referenced by one child container, then an explicit caller-supplied project mapping. If no exact C2 origin exists, a relation may use the selected focal system only as `requiresReview`. At C1, the explicitly selected focal system is the origin. Name similarity is never correlation evidence.

A generated external target and relation are `confirmed` only for high-confidence evidence, an exact correlated origin that is itself confirmed, and a non-empty observed target. Medium/low confidence, a review-pending source, or focal-system fallback keeps the assertion `requiresReview` with a reason. Database, cache, and storage resource details stay in root-system names and relation descriptions rather than becoming owned infrastructure containers.

Messaging creates a root logical provider only when `technology` identifies a supported broker/provider. Publish is emitted from the local correlated origin to that provider; consume is emitted from the provider to the local correlated consumer, while the wording remains `Consumes ... from ...`. Queue, topic, exchange, subscription, stream, and contract are relation details, never child containers or inferred remote services. Framework-only `masstransit`/`nservicebus`, custom `unknown` technology, and findings without a target stay as pending evidence because they do not prove a transport or resource identity.

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

## Semantic C3 HTTP/application policy

Semantic C3 remains additive to the stable C1/C2 model. For HTTP/application proposals:

- controller routes are aggregated by the declaring controller type, so multiple actions do not become arbitrary one-route components;
- Minimal API routes are aggregated by their enclosing semantic symbol (for example a top-level host or a route-mapping extension method);
- explicit endpoint handlers, typed endpoint dependencies, controller injection, DI registration and direct static symbol collaboration are structural signals that may support an `applicationService` candidate;
- a class name such as `Service`, `Handler` or `UseCase` is never sufficient by itself;
- a DI-registered type that is merely co-located with a Minimal API host may be proposed only as `requiresReview`, with the missing endpoint-level usage called out explicitly;
- worker, messaging and persistence responsibilities are excluded from this HTTP/application pass and are classified separately;
- internal C3 relations are not created by this step; relation construction has its own provenance/confidence policy.

Component IDs derive from stable source-symbol identity and category, and evidence references use Semantic C3 fact IDs. This keeps route aggregation and naming deterministic across repeated analysis while preserving the facts that motivated each proposal.

## Semantic C3 worker/messaging/persistence policy

The asynchronous Semantic C3 pass combines local .NET structure with the normalized Integration Evidence boundary instead of treating package/framework names as architecture.

- A concrete `BackgroundService` / `IHostedService` symbol is one worker candidate. `AddHostedService<T>` evidence is merged into the same symbol-derived candidate instead of creating a second component. Registration-only boundaries remain `requiresReview`.
- The worker host and its internal processing responsibility stay aggregated when the evidence exposes only one hosted-service boundary. They are split only when later evidence supports a distinct internal responsibility; class or method names alone do not justify that split.
- A messaging adapter requires both a concrete local publisher/consumer-shaped symbol and supported Integration Evidence for the same project/direction. The imported evidence supplies publish vs consume, provider/technology, resource and contract metadata.
- A messaging component is stable per local source symbol, direction/category and provider. Multiple findings for the same adapter merge provenance; different providers or distinct local adapter symbols remain distinct.
- Framework-only `masstransit` / `nservicebus`, unknown transports, missing targets, or unmatched local role evidence do not create a broker adapter or remote peer.
- `DbContext` inheritance is a strong local persistence-boundary signal and may produce a reviewable persistence candidate without modeling entities or `DbSet` values.
- A repository-shaped implementation is only promoted when its use is structurally observable from a worker/messaging boundary through DI registration plus constructor injection, or through direct local symbol collaboration. A type merely named `Repository` is insufficient.
- Database/cache package or Integration Evidence without a semantically linked local persistence responsibility does not fabricate a persistence component. Cache evidence is not reclassified as database persistence.
- External Semantic C3 relations are attached only when the same evidence ID already participates in an existing C1/C2 external relation involving the selected container. The C3 pass reuses that existing architecture element; it never invents the other endpoint.
- Low/medium confidence and all newly proposed C3 boundaries remain explicit `requiresReview` assertions. Resource, contract and provider detail remain provenance/relation metadata rather than extra C4 components.
- General component-to-component collaboration (worker → publisher, worker → application processing, application → persistence, and similar internal flow) is intentionally deferred to the dedicated internal-relation policy.

## Semantic C3 internal-relation confidence policy

Semantic C3 relations are composed only after component discovery. The relation builder is deterministic and bounded by explicit `MaxComponents` and `MaxRelations` limits.

| Structural evidence | Relation status |
| --- | --- |
| DI/handler wiring **and** direct symbol invocation between supported component responsibilities | `confirmed` |
| DI/handler wiring without direct invocation | `requiresReview` |
| direct invocation without DI/handler wiring | `requiresReview` |
| interface implementation only, type/name similarity only, or `ProjectReference` only | no runtime relation |
| existing exact C1/C2 external relation already `confirmed` with matching provenance | component → existing external element may be `confirmed` |
| external C1/C2 relation still under review | component → existing external element remains `requiresReview` |

Supported internal responsibility flows are intentionally narrow: HTTP endpoint → application service, application service → persistence/integration adapter, background worker → messaging publisher, and messaging consumer → application service. Other static references do not become architecture merely because they exist in the source graph.

Relations are merged by semantic source/destination/kind/description and evidence is unioned deterministically. Self-loops without explicit architectural semantics, missing endpoints and component-to-component edges that cross container boundaries are omitted. Reciprocal weak references do not manufacture cycles; a cycle is retained only when independently strong supported relations justify both directions.

The Semantic C3 evidence report lists each candidate/relation, its evidence locations and signal categories, missing strong signals, review reason and graph-budget/omission diagnostics without reproducing source bodies or configuration values.

## Out of scope

The C1/C2 mapping policy itself does not implement LikeC4 emission, CLI/MCP/Agent orchestration, AI calls, runtime tracing or automatic PR generation. The additive Semantic C3 policies above only define conservative component proposals; generation/orchestration is handled by later phases.


## Integrated Semantic C3 generation

The Semantic C3 policies above are connected to the normal inspection/generation path. Local inspection persists bounded `semanticC3Facts`; an optional compatible integration report persists normalized `externalIntegrationEvidence`. A reviewed C2 model can then select one or more containers and reuse those persisted facts offline.

The stable rule remains **class != component**. Responsibility candidates require architectural signals, and relations use the dedicated wiring/invocation confidence policy. Older v1 snapshots without semantic facts continue through the legacy generic selective-C3 grouping.

See [Semantic C3](semantic-c3.md) for the end-to-end contract, budgets, dogfooding result and known limitations.
