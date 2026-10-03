# Semantic C3 dogfood fixture

Reduced static fixture derived from `rodri-oliveira-dev/dotnet-observability-lab` main commit
`66b1dc7c789c96c521d08d7012155cf309f3d3f2`.

It preserves the architectural responsibilities used by the Phase 9 golden tests:

- Ingestion.Api: HTTP endpoint, ingestion use case, PostgreSQL persistence and Redis adapter.
- Ingestion.Outbox.Worker: hosted poller, RabbitMQ publisher and ingestion persistence.
- Consolidation.Api: HTTP read endpoint, query component and consolidation persistence.
- Consolidation.Worker: RabbitMQ consumer, application processor and consolidation persistence.

The fixture is intentionally smaller than the source repository and is never built or executed.
A few collaborations are written as statically resolvable type calls so the bounded Semantic C3
extractor can exercise its confidence policy deterministically. The real repository remains the
architectural reference; this fixture is the reproducible offline golden.

`TelemetryNames` is deliberate negative evidence: it must not become a C3 component.
