# Servidor MCP do Repo2C4

O Repo2C4 MCP é o **servidor local Model Context Protocol por stdio** do Repo2C4. Ele expõe evidências limitadas do repositório, geração determinística de LikeC4, relatório de evidências e validação LikeC4 para qualquer cliente MCP compatível, mantendo a interpretação arquitetural fora do servidor.

O servidor MCP **não** seleciona modelo de IA, não armazena credenciais de provider, não infere arquitetura sozinho e não expõe ferramenta genérica de leitura de arquivos. Conteúdo do repositório é dado não confiável. A raiz local autorizada, os schemas das tools e as regras de escrita protegida permanecem sob controle do host.

O fluxo normal é:

```text
cliente MCP
  -> inspect_repository
  -> get_evidence / get_snapshot quando necessário
  -> cliente propõe ArchitectureModel v1
  -> get_evidence_report
  -> generate_likec4 (dryRun=true)
  -> validate_likec4
  -> revisão humana / fronteira de aprovação do cliente
  -> generate_likec4 (dryRun=false, write=true)
  -> validate_likec4(destinationPath=...)
```

Para configuração específica de clientes, consulte o [quickstart de clientes MCP](mcp-quickstart.pt-BR.md). Para o fluxo evidence-first e o prompt reutilizável, consulte [Usando o Repo2C4 a partir de um cliente MCP](mcp-client.pt-BR.md).

## Quick Start

Pré-requisitos:

- .NET 10;
- um cliente MCP local compatível;
- LikeC4 somente quando `validate_likec4` for utilizado;
- uma única raiz de repositório explicitamente autorizada.

Depois que os pacotes do Repo2C4 estiverem publicados:

```bash
dotnet tool install --global Repo2C4.Mcp --version 1.0.1
npm install --global likec4@1.59.4

repo2c4-mcp --help
likec4 --version
```

O cliente deve iniciar o servidor MCP por stdio com a menor raiz necessária:

```text
command: repo2c4-mcp
args:
  --repository-root
  /caminho/absoluto/do/repositorio
```

O servidor não é uma aplicação de shell interativa. Quando iniciado diretamente no terminal sem um cliente MCP, ele aguarda tráfego do protocolo pela entrada padrão e reserva stdout para frames MCP.

Antes de uma publicação pública, execute o DLL compilado a partir de um checkout confiável:

```bash
dotnet build Repo2C4.slnx --configuration Release

dotnet src/Repo2C4.Mcp/bin/Release/net10.0/Repo2C4.Mcp.dll \
  --repository-root "/caminho/absoluto/do/repositorio"
```

A raiz também pode ser informada por `REPO2C4_REPOSITORY_ROOT`; `--repository-root` explícito tem precedência.

Exemplo em PowerShell:

```powershell
$env:REPO2C4_REPOSITORY_ROOT = (Resolve-Path ".\examples\fixtures\library-only").Path
repo2c4-mcp
```

Não autorize diretório pessoal, raiz do disco ou pasta-pai contendo repositórios não relacionados apenas por conveniência.

## Tools padrão

O servidor local normal expõe seis tools:

| Tool | Finalidade | Entradas importantes | Escrita |
| --- | --- | --- | --- |
| `inspect_repository` | Inspeciona um diretório dentro da raiz local autorizada e cria snapshot da sessão. | `repositoryPath` tem default `"."`; `maxFiles` tem default 1.000. | Somente leitura. |
| `get_evidence` | Pagina evidências v1 de um snapshot. | `snapshotId`, `category` exata opcional, `pathPrefix` opcional de metadados, `pageSize`, `cursor` opaco. | Somente leitura. |
| `get_snapshot` | Pagina metadados de arquivos ou diagnósticos sem retornar corpos de arquivos. | `snapshotId`, `section` = `files` ou `diagnostics`, `pathPrefix` opcional. | Somente leitura. |
| `get_evidence_report` | Devolve resumo limitado de proveniência/revisão para um modelo vinculado à sessão. | `snapshotId`, `ArchitectureModel` v1 completo. | Somente leitura. |
| `generate_likec4` | Gera LikeC4 C1/C2 determinístico e C3 seletivo opcional. | `snapshotId`, modelo completo, `destinationPath` opcional, `c3ContainerId` opcional. | Preview por padrão. Flags explícitas são obrigatórias para escrever. |
| `validate_likec4` | Valida modelo proposto ou workspace gerado existente pelo adaptador controlado da CLI oficial LikeC4. | `snapshotId`, modelo completo, `destinationPath` opcional, `c3ContainerId` opcional. | Somente leitura. |

`inspect_remote_repository` **não** é exposta por padrão. Ela aparece somente quando o host inicia explicitamente o servidor com `--allow-remote-acquisition`.

## Primeira inspeção local

Para inspecionar a própria raiz autorizada, chame:

```json
{
  "repositoryPath": ".",
  "maxFiles": 1000
}
```

`inspect_repository` retorna campos estruturados incluindo:

```jsonc
{
  "snapshotId": "<id-do-snapshot-da-sessao>",
  "repositoryId": "<id-estavel-do-repositorio>",
  "schemaVersion": "1",
  "expiresAtUtc": "<timestamp>",
  "fileCount": 0,
  "evidenceCount": 0,
  "diagnosticCount": 0,
  "evidenceCategories": [],
  "facts": [],
  "factsTruncated": false,
  "diagnostics": [],
  "diagnosticsTruncated": false
}
```

Os valores acima são placeholders; os nomes dos campos correspondem ao contrato real da resposta MCP. `repositoryId` é **retornado pela tool**. Ele não é uma entrada de `inspect_repository`.

Use `snapshotId` em todas as chamadas seguintes da mesma sessão stdio.

Snapshots ficam apenas em memória, são limitados à sessão stdio atual e expiram após 30 minutos. Até 16 snapshots de sessão são mantidos. Reconectar cria um novo armazenamento; um ID de sessão anterior não concede acesso ao snapshot antigo.

## Paginação segura de evidências

Quando a amostra inicial limitada de `facts` não for suficiente, chame `get_evidence`:

```json
{
  "snapshotId": "<snapshot-id>",
  "pageSize": 50
}
```

Use `category` somente para categoria exata e `pathPrefix` apenas como filtro de metadados relativo ao repositório. Nenhuma dessas opções abre ou devolve arbitrariamente código-fonte.

Se `nextCursor` for retornado, envie-o novamente sem alterações e com os **mesmos filtros**:

```jsonc
{
  "snapshotId": "<snapshot-id>",
  "pageSize": 50,
  "cursor": "<nextCursor>"
}
```

Cursores são tokens HMAC opacos vinculados à sessão, snapshot, tool e filtros. Cursor construído, alterado, usado com filtros diferentes ou fora de faixa falha com `cursor_invalid`.

Use `get_snapshot` quando precisar do inventário de arquivos ou diagnósticos do scanner:

```json
{
  "snapshotId": "<snapshot-id>",
  "section": "diagnostics",
  "pageSize": 50
}
```

A tool nunca retorna corpos de arquivos.

## Fronteira do ArchitectureModel

O servidor MCP não converte evidência em arquitetura sozinho. Um cliente — escrito por pessoa, determinístico ou assistido por IA — envia um `ArchitectureModel` v1 completo.

O modelo deve incorporar exatamente o snapshot canônico referenciado por `snapshotId` na sessão atual. O Repo2C4 valida o modelo e compara o snapshot embutido com o armazenado. Evidência fabricada, desatualizada ou de outra sessão é rejeitada com `snapshot_mismatch` ou `model_invalid`.

Evidência estática continua sendo evidência estática. Em especial:

- `ProjectReference` não prova comunicação em runtime;
- presença de pacote não prova topologia de deployment;
- categorias `.candidate` continuam sendo sinais candidatos;
- evidência estática `dotnet.*` ou `deployment.*` sozinha não pode confirmar uma fronteira C2 de deployment/container nem relação runtime.

Afirmações sem suporte devem permanecer `requiresReview`.

## Relatório de evidências

Antes da geração ou aprovação, o cliente pode chamar `get_evidence_report` com o `snapshotId` da sessão e o modelo completo. A resposta é apenas metadados e inclui:

- `confirmedAssertions`;
- `reviewRequiredAssertions`;
- `scanWarnings`;
- `missingOrigins`;
- `reviewRequiredIds` limitado;
- `warningCodes` limitado.

Ela não retorna corpos de código, valores de configuração ou caminhos absolutos e não grava arquivos.

Consulte [relatório de evidências e revisão arquitetural](evidence-report.md).

## Preview, validação e escrita protegida

`generate_likec4` usa preview por padrão:

```jsonc
{
  "snapshotId": "<snapshot-id>",
  "model": { "...": "ArchitectureModel v1 completo" },
  "dryRun": true,
  "write": false,
  "destinationPath": "docs/architecture/c1"
}
```

O modelo abreviado acima é apenas ilustrativo; clientes devem enviar o modelo v1 completo.

Informar `destinationPath` no dry-run é útil porque o Repo2C4 compara os arquivos propostos com o destino e retorna um plano estruturado de mudanças. A resposta inclui `files`, `changes` e `hasConflicts`, mas `written` permanece `false`.

Antes da escrita, valide a proposta sem destino:

```jsonc
{
  "snapshotId": "<snapshot-id>",
  "model": { "...": "ArchitectureModel v1 completo" }
}
```

Sem `destinationPath`, `validate_likec4` emite a proposta em workspace temporário isolado, executa o adaptador controlado do LikeC4 e remove o workspace temporário depois.

Uma escrita exige simultaneamente as três condições explícitas:

```jsonc
{
  "snapshotId": "<snapshot-id>",
  "model": { "...": "ArchitectureModel v1 completo" },
  "dryRun": false,
  "write": true,
  "destinationPath": "docs/architecture/c1"
}
```

`dryRun=false` sem `write=true`, `write=true` com `dryRun=true` ou escrita sem destino são rejeitados.

Após uma escrita bem-sucedida, valide o workspace existente:

```jsonc
{
  "snapshotId": "<snapshot-id>",
  "model": { "...": "ArchitectureModel v1 completo" },
  "destinationPath": "docs/architecture/c1"
}
```

Como em qualquer cliente MCP, a experiência de aprovação humana pertence ao cliente. O Repo2C4 exige argumentos explícitos e aplica proteções de filesystem, mas não afirma que uma chamada de protocolo, isoladamente, representa aprovação humana informada. Clientes devem fazer preview, validar e obter aprovação local antes de emitir a escrita protegida.

## Regeneração gerenciada

Quando `destinationPath` é informado, o dry-run compara a saída proposta com `.repo2c4-manifest.json` e os arquivos atuais. Cada arquivo é reportado como `added`, `modified`, `unchanged` ou `conflict`.

O Repo2C4 pode atualizar arquivos que ele próprio gerenciou quando eles ainda correspondem ao estado SHA-256 registrado no manifesto. Ele **não** sobrescreve:

- arquivo gerenciado alterado manualmente;
- arquivo gerenciado removido cujo estado não corresponde mais;
- alvo linkado/reparse point;
- arquivo não gerenciado que colida com um nome gerado.

Arquivos desconhecidos nunca são excluídos. As escritas são preparadas como conjunto gerenciado, aplicadas com verificações de conflito e rollback em best effort, e o manifesto é atualizado apenas pelo fluxo de commit gerenciado.

Se o estado mudar entre preview e apply, a escrita falha com `managed_output_conflict`; faça novo preview em vez de forçar.

## C3 seletivo

C1/C2 são o padrão. `generate_likec4` e `validate_likec4` em modo de proposta aceitam `c3ContainerId` opcional:

```jsonc
{
  "snapshotId": "<snapshot-id>",
  "model": { "...": "ArchitectureModel v1 C1/C2 completo" },
  "c3ContainerId": "container_api"
}
```

O ID deve identificar um container C2 existente no modelo informado. Somente esse container é expandido. Outros containers não recebem C3 automaticamente.

O builder C3 deriva candidatos de componentes limitados a partir das evidências associadas aos caminhos do container selecionado. Sinais candidatos/estáticos continuam revisáveis; container inexistente, evidência insuficiente, schema incompatível ou limites C3 falham de forma controlada em vez de inventar componentes.

Use o mesmo `c3ContainerId` ao validar a proposta gerada com C3.

## Aquisição Git pública opcional

Aquisição remota é desabilitada por padrão porque adiciona acesso de rede e exposição a conteúdo de terceiros.

Somente quando o host realmente precisar, inicie:

```bash
repo2c4-mcp \
  --repository-root "/caminho/absoluto/do/repositorio/local/autorizado" \
  --allow-remote-acquisition
```

Isso adiciona `inspect_remote_repository`. Ela aceita URL Git HTTPS pública, `gitRef` opcional e `maxFiles`. Credenciais embutidas, URLs SSH/file, repositórios privados/autenticados, submódulos e links simbólicos são rejeitados.

O repositório é adquirido em workspace temporário isolado, inspecionado pela mesma pipeline limitada de evidências e removido em seguida. URL/ref/commit resolvido retornam como proveniência da aquisição, separados das evidências arquiteturais.

Não adicione `--allow-remote-acquisition` à configuração normal do cliente quando inspeção remota não for necessária.

## Limite de protocolo e logs

O Repo2C4 usa o SDK .NET mantido `ModelContextProtocol.Core` sobre stdio.

- `stdout` é exclusivamente tráfego do protocolo MCP.
- Ajuda, erros de configuração e diagnósticos controlados usam `stderr`.
- Stack traces e detalhes brutos de exceções não são emitidos no protocolo.
- Ctrl+C ou cancelamento do host encerra o processo.
- Fechar stdin encerra a sessão e descarta o armazenamento de snapshots em memória.
- Nenhuma credencial de provedor de IA é exigida ou consumida pelo servidor.

Texto de repositório, instruções de README, descrições de evidência e diagnósticos são dados, não instruções do servidor.

## Limites

| Limite | Valor |
| --- | ---: |
| Timeout de inicialização | 30 segundos |
| Timeout de execução de tool | 30 segundos |
| Vida do snapshot | 30 minutos |
| Snapshots por sessão | 16 |
| Arquivos por inspeção | 1.000 |
| Página de recuperação padrão | 50 |
| Página de recuperação máxima | 100 |
| Itens em resumos | 20 |
| Resposta MCP estruturada | 1 MiB |

O scanner subjacente também mantém seus próprios limites de arquivos/bytes/entradas e exclusões de paths sensíveis.

## Erros controlados

Códigos esperados incluem:

- `repository_path_invalid`, `repository_unavailable`;
- `max_files_invalid`, `page_size_invalid`;
- `snapshot_not_found_or_expired`, `cursor_invalid`;
- `path_filter_invalid`, `category_invalid`, `section_invalid`;
- `model_invalid`, `snapshot_mismatch`, `model_review_required`;
- `write_not_authorized`, `destination_required`, `destination_invalid`, `managed_output_conflict`, `write_failed`;
- `validation_path_invalid`;
- `tool_timeout`;
- `response_limit_exceeded`.

A aquisição remota acrescenta códigos controlados próprios quando a tool opcional estiver habilitada.

## Raiz autorizada

A raiz configurada deve ser diretório local absoluto existente e não pode ser link simbólico, junction ou outro reparse point.

Paths locais de leitura e validação são resolvidos abaixo dessa raiz. Destinos de escrita protegida podem ser novos subdiretórios, mas componentes já existentes são revalidados. Traversal e componentes linkados existentes são rejeitados.

O scanner preserva exclusões obrigatórias para paths sensíveis/gerados e leituras limitadas. `.env`, nomes de credenciais/chaves, connection strings, secrets brutos e corpos arbitrários de código-fonte não são expostos como payloads MCP de evidência.

Esses controles reduzem escape acidental do filesystem, mas não prometem garantia atômica de no-follow contra filesystem malicioso concorrente. Use checkout confiável, estável e permissões mínimas.

## Troubleshooting

**Comando do servidor não encontrado**  
Confira `dotnet tool list --global`, o PATH do processo do cliente MCP e `repo2c4-mcp --help`.

**Cliente conecta, mas nenhuma tool aparece**  
Reinicie/recarregue o servidor pelo cliente e consulte os logs MCP do cliente. Confirme que stdout não está sendo envolvido por script que imprime banners ou diagnósticos.

**Raiz rejeitada**  
Use diretório absoluto existente que não seja symlink/junction/reparse-point root. Mantenha a raiz o mais restrita possível.

**Snapshot deixa de existir**  
Snapshots expiram após 30 minutos e desaparecem quando a sessão stdio termina. Execute `inspect_repository` novamente na sessão atual.

**`cursor_invalid`**  
Reutilize `nextCursor` exatamente e mantenha o mesmo snapshot, tool e filtros.

**Validação LikeC4 não inicia**  
Instale a CLI LikeC4 documentada e confirme que `likec4` está visível no PATH do processo MCP.

**`snapshot_mismatch`**  
O modelo completo deve incorporar exatamente o snapshot da sessão MCP atual. Refaça a inspeção e reconstrua/revise o modelo, em vez de reutilizar snapshot antigo.

**`managed_output_conflict`**  
Preserve a alteração humana, execute novo dry-run e revise o plano resultante. Não contorne o manifesto.

**Tool remota não aparece**  
Isso é esperado sem `--allow-remote-acquisition` explícito no processo do host.

## Limite arquitetural

`Repo2C4.Mcp` referencia Core; Core não referencia o SDK MCP. Os contratos versionados de repositório/evidência/modelo permanecem a fonte de verdade.

O servidor MCP é infraestrutura determinística ao redor desses contratos. Seleção de modelo, orquestração de IA, interpretação semântica da arquitetura e UX de aprovação humana pertencem aos clientes, como `Repo2C4.Agent`, clientes hospedados por VS Code/Claude ou outros consumidores MCP.
