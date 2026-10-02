# External integration interoperability boundary

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

This boundary keeps detector knowledge and SDK/provider recognition in DotNetRepoInspector while allowing Repo2C4 to preserve provenance and apply its own review policy. Parser implementation, conversion into the Repo2C4 `Evidence` contract, C1/C2 mapping, and CLI/MCP/Agent exposure are deliberately deferred to the subsequent phase issues.
