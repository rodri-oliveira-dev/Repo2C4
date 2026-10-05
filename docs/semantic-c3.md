# Semantic C3

Semantic C3 turns bounded .NET structural facts into **reviewable architectural component proposals** for explicitly selected C2 containers. It is additive to the stable C1/C2 contract and follows the same evidence-first rule: observed structure is provenance, not architectural truth.

## Class is not a component

Repo2C4 never promotes every class to C4. A type becomes a Semantic C3 candidate only when structural evidence supports an architectural responsibility:

- `httpEndpoint`: Minimal API or controller boundary;
- `applicationService`: application/use-case/handler reached through observable endpoint or consumer wiring/collaboration;
- `backgroundWorker`: `BackgroundService` / `IHostedService`;
- `messagingConsumer` / `messagingPublisher`: local transport-shaped type correlated with normalized integration evidence;
- `persistenceAdapter`: observed `DbContext` or structurally used repository responsibility;
- `integrationAdapter`: supported HTTP/cache/storage integration correlated to one concrete local type.

Names improve descriptions but never prove a component. Utility, telemetry and unrelated helper types remain outside the C3 graph without stronger evidence.

## Inspection and persisted facts

Local `inspect` and MCP `inspect_repository` run the bounded repository inspection plus a bounded C# structural pass. The snapshot may contain additive `semanticC3Facts` with stable symbol identity, type/method/base/interface facts, Minimal API/controller boundaries, DI/constructor wiring, hosted-service, persistence, messaging-role and conservative statically resolvable collaboration facts.

These records are repository-relative metadata only. They do **not** contain source bodies, arbitrary literals, secret values or absolute paths.

When a compatible DotNetRepoInspector `InspectionReport` schema 1.6+ is supplied through `--integration-report` / `integrationReportPath`, normalized sanitized findings are also retained as `externalIntegrationEvidence`. Later offline generation can correlate PostgreSQL, Redis, RabbitMQ and other supported peers without re-reading the original report.

Older snapshots without `semanticC3Facts` remain compatible and use the legacy generic selective-C3 grouping.

## From reviewed C2 to C3

Semantic C3 never chooses C2 container boundaries. The input remains a reviewed C2 `ArchitectureModel`. For each explicitly selected container, Repo2C4 scopes the persisted facts to its evidence-backed projects, proposes responsibility candidates, correlates supported integrations, composes conservative relations, then emits through the normal C3/LikeC4 workspace.

All selected containers share exactly one reviewed C2 model/snapshot.

## Confidence and review status

Component boundaries remain `requiresReview`: static structure can support a useful candidate but cannot prove the only correct architectural decomposition.

Internal relations are conservative. DI/handler/parameter wiring is one signal; a statically resolved symbol invocation is another. Supported pairs such as HTTP → application, application → persistence, worker → publisher and consumer → application are confirmed only when the required structural signals converge. Names, package presence, `ProjectReference` and isolated type declarations do not confirm runtime flow. Weak reciprocal edges, self-loops and cross-container internal edges are omitted rather than guessed.

External C3 relations reuse an existing C1/C2 peer and relation evidence. Semantic C3 never invents PostgreSQL, Redis, brokers or HTTP systems from names alone; newly associated external C3 edges remain reviewable.

## Provenance

When Semantic C3 is active, generation adds:

- `c3.views.c4`: one view per selected container;
- `semantic-c3-evidence-report.md`: component/relation IDs, categories, repository-relative locations, signal classes, missing confidence signals, review status and bounded graph diagnostics.

The regular `evidence-report.md` remains the C1/C2 review artifact. Neither report copies source bodies or configuration values.

## Single and multi-container

Single selection remains compatible:

```bash
repo2c4 generate --model architecture.c2.json --output generated \
  --c3-container el_ingestion_api
```

Repeat the option for one workspace with multiple C3 views:

```bash
repo2c4 generate --model architecture.c2.json --output generated \
  --c3-container el_ingestion_api \
  --c3-container el_ingestion_outbox_worker \
  --c3-container el_consolidation_api \
  --c3-container el_consolidation_worker \
  --apply
```

Selections are deduplicated and canonicalized; argument order does not affect output. Unknown/non-container IDs and C3 over C1 fail before managed output is written.

MCP uses `c3Containers` (legacy `c3ContainerId` remains for one selection). Agent repeated `--c3-container` values are only an authorization allow-list; the Agent must request an evidence-supported subset.

## Budgets

| Boundary | Limit |
| --- | ---: |
| C# files | 512 |
| Total accepted C# bytes | 8 MiB |
| Symbols | 4,096 |
| Structural relation facts | 8,192 |
| Persisted semantic facts | 20,000 |
| Semantic diagnostics | 128 |
| Standalone semantic timeout | 5 s |
| Integrated inspection semantic timeout | 10 s |
| Components per selected container | 64 |
| Relations per selected container | 128 |
| MCP C3 selections per call | 8 |
| MCP total components / relations per call | 256 / 512 |
| Agent authorized C3 container IDs | 8 |

Cancellation is checked throughout scanning/parsing. Partial or malformed source produces controlled diagnostics; Repo2C4 does not build or execute the inspected repository.

## Dogfooding

Phase 9 uses a reduced offline golden derived from `dotnet-observability-lab` main commit `66b1dc7c789c96c521d08d7012155cf309f3d3f2`.

The v1.1.0 baseline for `Ingestion.Api` produced:

```text
HTTP interface
Integration adapter
Application dependency
```

The Semantic C3 golden requires:

| Container | Architectural responsibilities |
| --- | --- |
| `Ingestion.Api` | HTTP ingestion boundary, application handler/use case, persistence, Redis adapter |
| `Ingestion.Outbox.Worker` | background/outbox poller, RabbitMQ publisher, ingestion persistence |
| `Consolidation.Api` | HTTP read boundary, query/application responsibility, consolidation persistence |
| `Consolidation.Worker` | RabbitMQ consumer, transport-neutral processor, consolidation persistence |

The golden verifies useful internal relations, reviewed PostgreSQL/Redis/RabbitMQ peers, no Redis edge for `Consolidation.Worker`, no utility/telemetry promotion, four C3 views in one workspace, deterministic managed regeneration and the same semantic path through Core, CLI and real MCP inspection. Agent multi-C3 remains covered by its deterministic real-MCP harness.

See [the fixture](../examples/fixtures/semantic-c3-dogfood/README.md) and [the v1.1.0 baseline](semantic-c3-baseline.md).

## Known limitations and security

Semantic C3 is bounded static analysis, not a universal call graph or runtime tracer. It does not build/execute repository code, evaluate arbitrary MSBuild logic, prove deployment/runtime communication, understand every framework convention, resolve reflection/dynamic calls universally, promote ambiguous type-to-component mappings, or replace architectural review.

Run inspection against a trusted, stable, preferably read-only checkout. Repository content is untrusted input; path containment, file/symbol/fact budgets, parser timeouts, literal masking and sanitized diagnostics are part of the security boundary. See [CLI](cli.md), [MCP](mcp.md) and [distribution](distribution.md).
