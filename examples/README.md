# Reproducing evidence extraction locally

The checked-in [multi-project fixture](fixtures/multiproject/Acme.slnx) contains a Web API declaration, shared library, worker declaration, build-time project reference, Npgsql/RabbitMQ/Redis package references, HTTP/worker source signals and Docker/compose manifests. [Library-only fixture](fixtures/library-only/OnlyLib.csproj) shows that a library need not imply a running application. These fixtures do not need credentials, network, Docker or build execution.

Install the .NET 10 SDK in `global.json` and run from the repository root:

```bash
dotnet restore Repo2C4.slnx --locked-mode
dotnet test Repo2C4.slnx --configuration Release
```

`RepositoryFactExtractorTests` invokes the Core API against temporary fixtures covering these cases, verifies line numbers and resolved build-time project references, and checks that package/source signals are only candidates, never proven runtime communication. A checked-in [illustrative v1 snapshot](snapshot.v1.json) represents the **library-only fixture** with a measured 121-byte project file. The example uses readable, valid sample evidence IDs; the actual scanner generates SHA-256-based IDs with `StableIds.ForEvidence`, so the example is not claimed to be a byte-for-byte generated capture.

To call the API from your own .NET host, reference `src/Repo2C4.Core/Repo2C4.Core.csproj` and use:

```csharp
using Repo2C4.Core.Contracts;
using Repo2C4.Core.Inspection;

string authorizedRoot = Path.GetFullPath("examples/fixtures/multiproject");
RepositoryScanOptions options = new(authorizedRoot, "sample_repo");
RepositorySnapshot snapshot = RepositoryFactExtractor.Extract(options, cancellationToken);
string json = ContractJson.SerializeSnapshot(snapshot);
Console.WriteLine(json);
```

The host must supply its cancellation token and explicit local root. During this foundation phase, the CLI `inspect` command and MCP transport are not yet implemented. The scanner inventories file metadata only; the extractor subsequently reads only accepted project/solution/source files, capped at **512 KiB per file** even if the caller raises the scanner's maximum file size. If a file changes, becomes inaccessible, contains invalid XML/UTF-8 or references an un-inventoried project, the extractor reports a non-secret-bearing `extract.*` diagnostic and does not manufacture a fact. Prohibited XML DTDs and external entities are never processed.

Evidence categories `dotnet.project.reference` and `dotnet.solution.project` describe static declarations; categories ending in `.candidate` are unverified technology/host signals. Package presence is **not** evidence of an HTTP/broker/database runtime relation. There are no architecture elements or relations in this snapshot. A downstream reviewer must explicitly verify runtime communications before producing a confirmed LikeC4 relation.

## Optional future integration

An integration with [DotNetRepoInspector](https://github.com/rodri-oliveira-dev/DotNetRepoInspector) is not introduced in this phase. The extractor intentionally reads only the minimal declarations necessary for evidence provenance, without project compilation, MSBuild evaluation, dependency analysis or a generic repository-inspection framework. Later phases may provide an **optional adapter** for richer inspection, with an explicit data contract, consent and equivalent path/size/secret boundaries. Core and the tests do not require that dependency.
