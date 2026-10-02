# Boundary de interoperabilidade de integrações externas

[English](external-integration-boundary.md)

O DotNetRepoInspector descobre integrações. O Repo2C4 interpreta esses findings como evidência arquitetural.

O Repo2C4 consome somente o JSON público `InspectionReport` schema `1.6+`. O Core não referencia assemblies do DotNetRepoInspector e nenhum host inicia seu processo. O adapter preserva projeto/origem, direction, technology, target/resource, contract, confidence e sinais determinísticos; campos aditivos são ignorados.

A fixture canônica vem de `rodri-oliveira-dev/DotNetRepoInspector` tag `v1.6.5`, commit `50f85ff0a314a860d3cb75c62e5515c3e9883742`, arquivo `docs/en/schema/examples/inspection-v1.example.json`. O cenário reproduzível em `examples/fixtures/external-integrations-e2e` usa a mesma forma pública e registra todos os campos consumidos.

```bash
dotnet tool install --global DotNetRepoInspector --version 1.6.5
dotnet repo-inspect /caminho/absoluto/do/repositorio \
  --discover-integrations \
  --output /caminho/absoluto/do/repositorio/artifacts/inspection.json

repo2c4 inspect \
  --repository /caminho/absoluto/do/repositorio \
  --integration-report /caminho/absoluto/do/repositorio/artifacts/inspection.json \
  --output snapshot.json
```

No MCP, use `inspect_repository` com `repositoryPath="."` e `integrationReportPath="artifacts/inspection.json"`, depois consulte `external.*` por `get_evidence`. No Agent, acrescente `--integration-report artifacts/inspection.json`; o host injeta somente esse caminho autorizado no MCP e o Agent não lê o report.

`confirmed` exige confidence alta, origem local exata e target/provider sustentado. Target ausente, framework de mensageria sem transport, confidence média/baixa e origem ambígua ficam `requiresReview`. Publish aponta da origem local para o provider; consume aponta do provider para a origem local. Nenhuma direção inventa a outra ponta remota.

O report é entrada não confiável: limite padrão de 8 MiB, 5.000 findings, 32 sinais por finding e 512 caracteres por texto. Paths absolutos/traversal/links são rejeitados. JSON bruto, source body, valores de configuração, connection strings, credentials, queries e payloads não entram no snapshot ou nos diagnósticos.
