# External integration interoperability boundary

[Português (Brasil)](external-integration-boundary.pt-BR.md)

## Decision

Repo2C4 consumes external integration findings only through the public JSON contract emitted by DotNetRepoInspector `InspectionReport`. DotNetRepoInspector owns technical discovery; Repo2C4 owns the conservative architectural interpretation that may later produce Repo2C4 evidence and reviewed C1/C2 assertions.

The initial boundary accepts schema `1.6` and later additive `1.x` versions. Unknown fields are ignored. Reports older than `1.6`, malformed version strings, and different major versions are incompatible and must produce controlled diagnostics without echoing report content. A future breaking schema requires explicit importer support.

`IExternalIntegrationEvidenceSource` reads an explicitly supplied JSON stream and returns normalized metadata. The Core neither references DotNetRepoInspector assemblies nor starts its executable. Hosts decide whether and how a report is obtained; importing one performs no repository scan, process execution, network request, or automatic architectural generation.

## Normalized evidence and provenance

For each accepted integration finding, the internal boundary preserves:

- deterministic Repo2C4 evidence ID and the original finding ID when supplied;
- repository-relative project and source paths plus the one-based source line;
- kind, direction, technology, target, resource type and configuration key name;
- contract, message, or handler identity;
- confidence and deterministic signals.

Paths must use `/`, be relative to the inspected repository, and contain no traversal. Absolute, drive-qualified, backslash, empty-segment, or control-character paths are rejected. Unknown additive JSON fields are not retained. Findings and signals are canonicalized with ordinal ordering by the importer so source enumeration order cannot affect output.

Correlation requires every finding's project and source paths to exist in the local `RepositorySnapshot`, and the project must also occur in the report's project inventory. Callers that know the authoritative repository name or commit supply them through `ExternalIntegrationImportContext`; a missing or different supplied identity rejects the report. When neither value is available, successful path reconciliation is accepted with an explicit `external.correlation.pathOnly` diagnostic rather than silently claiming commit-level correlation.

## Confidence and architectural review

Imported evidence remains an observation; it is not itself a confirmed C4 assertion. A later mapping may mark an assertion `confirmed` only when high-confidence evidence establishes the repository origin, integration kind and direction, and enough target or provider identity for the represented endpoint. Medium or low confidence, an unknown kind/direction, or incomplete endpoint identity remains `requiresReview` with an explicit reason. Queue, topic, exchange, stream, subscription, message, or handler names describe the known relation and never prove a remote producer, consumer, owner, deployment, or container.

## Trust and resource limits

The JSON stream is untrusted input. The default boundary budgets are 8 MiB per report, 5,000 findings, 32 signals per finding, and 512 characters per retained text field. Implementations may accept stricter positive limits but must never read beyond the configured byte budget. Cancellation must be honored.

The normalized result never carries raw JSON, source bodies, configuration values, connection strings, credentials, authentication data, queries, payloads, or exception text derived from the report. `configurationKey` is a key name only. Diagnostics use stable codes, structural paths, and fixed non-sensitive messages; they do not repeat rejected values or report fragments.

## Consequences

This boundary keeps detector knowledge and SDK/provider recognition in DotNetRepoInspector while allowing Repo2C4 to preserve provenance and apply its own review policy. The shared Core importer and mapper are exposed by CLI, MCP, and Agent without duplicating detectors or taking a runtime dependency on DotNetRepoInspector.

The canonical contract fixture comes from `rodri-oliveira-dev/DotNetRepoInspector` tag `v1.6.5`, commit `50f85ff0a314a860d3cb75c62e5515c3e9883742`, file `docs/en/schema/examples/inspection-v1.example.json`. The reproducible two-project scenario in `examples/fixtures/external-integrations-e2e` documents every consumed field and extends only the released public shape.

## End-to-end usage

DotNetRepoInspector discovers integrations. Repo2C4 interprets those findings as architecture evidence.

```bash
dotnet tool install --global DotNetRepoInspector --version 1.6.5
dotnet repo-inspect /absolute/path/to/repository \
  --discover-integrations \
  --output /absolute/path/to/repository/artifacts/inspection.json

repo2c4 inspect \
  --repository /absolute/path/to/repository \
  --integration-report /absolute/path/to/repository/artifacts/inspection.json \
  --output snapshot.json
```

For MCP, call `inspect_repository` with `repositoryPath="."` and `integrationReportPath="artifacts/inspection.json"`, then retrieve `external.*` with `get_evidence`. For Agent, add `--integration-report artifacts/inspection.json`; the host injects that authorized path into the same MCP inspection and the Agent never reads the report directly.

High confidence plus an exact local origin and a supported target/provider can support `confirmed`. Missing targets, framework-only messaging, medium/low confidence, or ambiguous origin remain `requiresReview`. Publish points from the local origin to the provider; consume points from the provider to the local origin. Neither direction invents the opposite remote endpoint.
