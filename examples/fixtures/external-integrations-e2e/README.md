# External integration interoperability fixture

This fixture follows the public DotNetRepoInspector `InspectionReport` schema `1.6` contract shipped at tag `v1.6.5`, commit `50f85ff0a314a860d3cb75c62e5515c3e9883742`.

Contract source: `rodri-oliveira-dev/DotNetRepoInspector`, `docs/en/schema/examples/inspection-v1.example.json`. The official canonical example is preserved as equivalent formatted JSON in `tests/fixtures/external-integrations/dotnetrepoinspector-v1.6.5-canonical.json`; this E2E fixture extends its public shape with findings from the same released contract to exercise two project origins.

The adapter consumes only `schemaVersion`, repository `name`/`commitSha`, integration discovery completion/truncation, and each integration's `id`, `projectPath`, `kind`, `direction`, `technology`, `target`, `resourceType`, `configurationKey`, `contract`, `source.path`, `source.line`, `confidence`, and `signals`. Other fields remain additive and are ignored. Values are synthetic identifiers, never credentials, connection strings, payloads, or source bodies.
