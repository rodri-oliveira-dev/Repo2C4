# Semantic C3

O Semantic C3 transforma fatos estruturais .NET limitados em **propostas revisáveis de componentes arquiteturais** para containers C2 selecionados explicitamente. Ele é aditivo ao contrato C1/C2 estável e mantém a regra evidence-first: estrutura observada é proveniência, não verdade arquitetural.

## Classe não é componente

O Repo2C4 nunca promove toda classe para C4. Um tipo só vira candidato Semantic C3 quando evidência estrutural sustenta uma responsabilidade:

- `httpEndpoint`: fronteira Minimal API ou Controller;
- `applicationService`: application/use case/handler alcançado por wiring/colaboração observável a partir de endpoint ou consumer;
- `backgroundWorker`: `BackgroundService` / `IHostedService`;
- `messagingConsumer` / `messagingPublisher`: tipo local com papel de transporte correlacionado a evidência normalizada de integração;
- `persistenceAdapter`: `DbContext` observado ou repository estruturalmente utilizado;
- `integrationAdapter`: integração HTTP/cache/storage suportada correlacionada a um único tipo concreto local.

Nomes ajudam na descrição, mas nunca provam um componente. Utilitários, telemetria e helpers não relacionados ficam fora do C3 sem evidência mais forte.

## Inspeção e fatos persistidos

`inspect` local e `inspect_repository` MCP executam a inspeção limitada normal e uma passagem estrutural C# limitada. O snapshot pode conter `semanticC3Facts` aditivos com identidade estável de símbolo, fatos de tipo/método/base/interface, fronteiras Minimal API/Controller, DI/injeção, hosted service, persistence, mensageria e colaborações estaticamente resolvíveis de forma conservadora.

Esses registros contêm somente metadados relativos ao repositório. Eles **não** contêm corpos de source, literais arbitrários, segredos nem caminhos absolutos.

Com `--integration-report` / `integrationReportPath` e um `InspectionReport` DotNetRepoInspector schema 1.6+ compatível, findings normalizados e sanitizados também são persistidos como `externalIntegrationEvidence`. A geração offline posterior consegue correlacionar PostgreSQL, Redis, RabbitMQ e outros peers suportados sem reler o report original.

Snapshots antigos sem `semanticC3Facts` continuam compatíveis e usam o agrupamento C3 seletivo genérico legado.

## Do C2 revisado ao C3

Semantic C3 não escolhe fronteiras C2. A entrada continua sendo um `ArchitectureModel` C2 revisado. Para cada container selecionado, o Repo2C4 limita os fatos aos projetos sustentados pelas evidências daquele container, propõe responsabilidades, correlaciona integrações suportadas, compõe relações conservadoras e emite pelo workspace C3/LikeC4 normal.

Todos os containers selecionados compartilham exatamente o mesmo modelo/snapshot C2 revisado.

## Confiança e review status

Fronteiras de componente permanecem `requiresReview`: estrutura estática sustenta um candidato útil, mas não prova a única decomposição correta.

Relações internas são conservadoras. Wiring por DI/handler/parâmetro é um sinal; invocação de símbolo resolvida estaticamente é outro. Pares suportados como HTTP → application, application → persistence, worker → publisher e consumer → application só são confirmados quando os sinais exigidos convergem. Nome, pacote, `ProjectReference` ou declaração isolada de tipo não confirmam fluxo runtime. Self-loops, ciclos fracos recíprocos e edges internas cruzando containers são omitidos em vez de adivinhados.

Relações C3 externas reutilizam peer e evidência já existentes em C1/C2. O Semantic C3 não inventa PostgreSQL, Redis, broker ou sistema HTTP pelo nome; novas associações C3 externas continuam revisáveis.

## Proveniência

Quando Semantic C3 está ativo, a geração adiciona:

- `c3.views.c4`: uma view por container;
- `semantic-c3-evidence-report.md`: IDs, categorias, locais relativos, classes de sinal, sinais de confiança ausentes, review status e diagnósticos limitados.

O `evidence-report.md` normal continua sendo o artefato C1/C2. Nenhum relatório copia corpos de source ou valores de configuração.

## Single e multi-container

Uma seleção continua compatível:

```bash
repo2c4 generate --model architecture.c2.json --output generated \
  --c3-container el_ingestion_api
```

Repita a opção para um workspace com várias views C3:

```bash
repo2c4 generate --model architecture.c2.json --output generated \
  --c3-container el_ingestion_api \
  --c3-container el_ingestion_outbox_worker \
  --c3-container el_consolidation_api \
  --c3-container el_consolidation_worker \
  --apply
```

Seleções são deduplicadas e canonizadas; a ordem não altera a saída. IDs desconhecidos/não-container e C3 sobre C1 falham antes da escrita gerenciada.

MCP usa `c3Containers` (`c3ContainerId` legado permanece para uma seleção). No Agent, `--c3-container` repetido é somente allow-list de autorização; o Agent deve solicitar um subconjunto sustentado por evidência.

## Budgets

| Fronteira | Limite |
| --- | ---: |
| Arquivos C# | 512 |
| Bytes C# totais aceitos | 8 MiB |
| Símbolos | 4.096 |
| Fatos estruturais de relação | 8.192 |
| Fatos semânticos persistidos | 20.000 |
| Diagnósticos semânticos | 128 |
| Timeout semântico isolado | 5 s |
| Timeout semântico na inspeção integrada | 10 s |
| Componentes por container selecionado | 64 |
| Relações por container selecionado | 128 |
| Seleções C3 por chamada MCP | 8 |
| Componentes / relações totais por chamada MCP | 256 / 512 |
| IDs C3 autorizados no Agent | 8 |

Cancelamento é verificado durante scan/parser. Source parcial ou malformado gera diagnóstico controlado; o Repo2C4 não compila nem executa o repositório inspecionado.

## Dogfooding

A Fase 9 usa um golden offline reduzido derivado da `main` de `dotnet-observability-lab`, commit `66b1dc7c789c96c521d08d7012155cf309f3d3f2`.

A baseline v1.1.0 de `Ingestion.Api` produzia:

```text
HTTP interface
Integration adapter
Application dependency
```

O golden Semantic C3 exige:

| Container | Responsabilidades arquiteturais |
| --- | --- |
| `Ingestion.Api` | fronteira HTTP de ingestão, handler/use case application, persistence, adapter Redis |
| `Ingestion.Outbox.Worker` | worker/poller de Outbox, publisher RabbitMQ, persistence de ingestão |
| `Consolidation.Api` | fronteira HTTP de leitura, query/application, persistence de consolidação |
| `Consolidation.Worker` | consumer RabbitMQ, processor independente de transporte, persistence de consolidação |

O golden verifica relações internas úteis, peers PostgreSQL/Redis/RabbitMQ revisados, ausência de Redis em `Consolidation.Worker`, ausência de promoção de utilitários/telemetria, quatro views C3 no mesmo workspace, regeneração gerenciada determinística e o mesmo caminho semântico por Core, CLI e inspeção MCP real. O multi-C3 do Agent continua coberto pelo harness determinístico com MCP real.

Veja a [fixture](../examples/fixtures/semantic-c3-dogfood/README.md) e a [baseline v1.1.0](semantic-c3-baseline.md).

## Limitações conhecidas e segurança

Semantic C3 é análise estática limitada, não call graph universal nem tracing runtime. Ele não compila/executa código do repositório, não avalia MSBuild arbitrário, não prova topologia/comunicação runtime, não entende toda convenção de framework, não resolve reflection/dynamic universalmente, não promove mapeamentos ambíguos tipo→componente e não substitui revisão arquitetural.

Execute a inspeção em checkout confiável, estável e preferencialmente somente leitura. Conteúdo do repositório é entrada não confiável; contenção de caminhos, budgets de arquivos/símbolos/fatos, timeouts de parser, masking de literais e diagnósticos sanitizados fazem parte da fronteira de segurança. Veja [CLI](cli.pt-BR.md), [MCP](mcp.pt-BR.md) e [distribuição](distribution.pt-BR.md).
