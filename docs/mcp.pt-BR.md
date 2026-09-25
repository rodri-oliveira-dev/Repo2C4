# Servidor MCP stdio, ferramentas de inspeção e política de acesso local

A Fase 3 executa o Repo2C4 como servidor MCP local por stdio. A issue #13 estabeleceu transporte e limite de filesystem, a #14 adicionou inspeção/evidências, a #15 adicionou geração/validação LikeC4 protegida e a #16 documenta e testa o fluxo completo com cliente independente de fornecedor.

## Iniciar o servidor

Compile a solução e informe exatamente uma raiz local autorizada:

```bash
dotnet src/Repo2C4.Mcp/bin/Release/net10.0/Repo2C4.Mcp.dll \
  --repository-root /caminho/absoluto/do/repositorio
```

Como alternativa de configuração local, defina `REPO2C4_REPOSITORY_ROOT` e omita a opção de linha de comando. A linha de comando tem precedência. O servidor não exige chave de provedor de IA, conta externa, endpoint de rede ou configuração de modelo.

## Limite de protocolo e logs

O host usa o SDK .NET mantido `ModelContextProtocol.Core` com transporte stdio. `stdout` fica reservado exclusivamente às mensagens do protocolo MCP. Ajuda, falhas de configuração e diagnósticos controlados são escritos em `stderr`; detalhes de exceção e stack traces não são emitidos no canal do protocolo.

O processo trata cancelamento pelo token do host e por Ctrl+C. O fechamento de stdin encerra a sessão e descarta o armazenamento de snapshots em memória. Texto do repositório é dado não confiável e não pode substituir instruções do servidor. O servidor não incorpora provedor de IA e não expõe leitura genérica de arquivos.

## Ferramentas de inspeção

A issue #14 expõe exatamente três ferramentas somente leitura:

| Ferramenta | Finalidade | Entradas principais |
| --- | --- | --- |
| `inspect_repository` | Inspeciona um diretório autorizado com o scanner/extrator existente do Core e cria snapshot v1 da sessão. | `repositoryPath` relativo à raiz autorizada, `maxFiles` de 1 a 1.000. |
| `get_evidence` | Recupera uma página limitada de `Evidence` v1. | `snapshotId`, `category` exata opcional, `pathPrefix` opcional apenas sobre metadados, `pageSize` 1–100, `cursor` opaco. |
| `get_snapshot` | Recupera metadados de arquivos ou diagnósticos do snapshot. Nunca retorna conteúdo dos arquivos. | `snapshotId`, `section` = `files` ou `diagnostics`, `pathPrefix` opcional, `pageSize` 1–100, `cursor` opaco. |

`inspect_repository` retorna ID do snapshot, IDs de repositório/schema, expiração, contagens, categorias de evidência e uma pequena amostra limitada de fatos/diagnósticos. O cliente usa as ferramentas de recuperação para páginas adicionais, sem receber o repositório ou o código-fonte inteiro em uma única resposta.

Snapshots existem somente na sessão stdio atual e expiram após 30 minutos. O ID do snapshot é um hash estável do snapshot v1 canônico; possuir um ID obtido em outra sessão não concede acesso, porque cada sessão mantém seu próprio armazenamento. Cursores de paginação são tokens HMAC opacos vinculados à sessão, snapshot, ferramenta e filtros. Cursores alterados, usados com filtros diferentes ou fora de faixa falham com `cursor_invalid`.

## Relatório de evidências

A issue #17 adiciona a ferramenta somente leitura `get_evidence_report`. Ela recebe `snapshotId` e um `ArchitectureModel` v1 completo, valida o vínculo com o snapshot da sessão e devolve apenas um resumo limitado: contagens, IDs de afirmações que exigem revisão e códigos de aviso. O Markdown completo permanece como artefato da CLI. A resposta MCP não inclui corpos de código, valores de configuração ou caminhos absolutos, não escreve arquivos e não chama IA.

Consulte [relatório de evidências e revisão arquitetural](evidence-report.md).

## Ferramentas de geração e validação LikeC4

A issue #15 adiciona duas ferramentas, mantendo a interpretação arquitetural no cliente MCP:

| Ferramenta | Finalidade | Comportamento de escrita |
| --- | --- | --- |
| `generate_likec4` | Recebe um `ArchitectureModel` v1 completo e o `snapshotId` da sessão, confirma que o snapshot embutido é exatamente o snapshot armazenado, preserva os limites de revisão e gera saídas deterministicamente. | O padrão é `dryRun=true`. Com `destinationPath`, a resposta inclui resumo estruturado das mudanças. Escrita exige `dryRun=false`, `write=true`, ausência de conflitos e o mesmo destino explícito. |
| `validate_likec4` | Executa o adaptador controlado da CLI oficial LikeC4 sobre os arquivos propostos ou sobre um destino autorizado existente. | Somente leitura. Sem `destinationPath`, valida workspace temporário isolado; com destino, valida um diretório existente dentro da raiz autorizada. |

O servidor não confia no snapshot contido no modelo. O `ContractValidator` precisa aceitar o modelo e o snapshot v1 canônico do modelo deve ser idêntico ao snapshot apontado pelo `snapshotId` da sessão. Isso bloqueia evidência fabricada mesmo quando um modelo fabricado é internamente consistente.

A fronteira MCP também preserva a política de mapeamento C4: container C2 ou relação runtime não podem ser `confirmed` quando o suporte consiste apenas em evidências estáticas `dotnet.*` ou `deployment.*`. Essas afirmações permanecem `requiresReview`. O cliente decide como interpretar a evidência e qual IA, se houver, utilizar; o servidor nunca seleciona nem chama provedor de IA.

A regeneração protegida resolve apenas diretórios filhos da raiz configurada, rejeita traversal e componentes linkados existentes, mantém `.repo2c4-manifest.json`, compara hashes atuais com o último snapshot gerado e classifica cada arquivo como `added`, `modified`, `unchanged` ou `conflict`. Arquivos editados manualmente ou colisões não gerenciadas nunca são sobrescritos. A escrita é preparada de forma transacional, com rollback em best effort, e o manifesto só é atualizado após a preparação bem-sucedida. Assim como na leitura, verificações gerenciadas não garantem no-follow atômico contra filesystem malicioso concorrente.

## Erros controlados e limites

Descrições e schemas MCP informam parâmetros e limites. Erros controlados esperados incluem:

- `repository_path_invalid` para path fora da raiz, inexistente, traversal ou componente linkado;
- `repository_unavailable` quando o repositório não pode ser inspecionado com segurança;
- `max_files_invalid` e `page_size_invalid` para limites inválidos;
- `snapshot_not_found_or_expired` para snapshot ausente, expirado ou de outra sessão;
- `cursor_invalid` para cursor malformado, alterado, incompatível ou fora de faixa;
- `path_filter_invalid`, `category_invalid` e `section_invalid` para filtros inválidos;
- `model_invalid`, `snapshot_mismatch` e `model_review_required` para modelos inseguros ou sem suporte suficiente;
- `write_not_authorized`, `destination_required`, `destination_invalid` e `managed_output_conflict` para falhas de escrita/regeneração protegida;
- `validation_path_invalid` para diretório de validação ausente ou inseguro;
- `tool_timeout` quando a inspeção excede 30 segundos;
- `response_limit_exceeded` quando a resposta estruturada ultrapassaria o orçamento MCP de 1 MiB.

O teto permanece em 1.000 arquivos inspecionados e 1 MiB por resposta MCP. Páginas de recuperação usam 50 itens por padrão e no máximo 100.

## Raiz autorizada do repositório

A raiz configurada deve ser um diretório absoluto existente, sem traversal, e não pode ser link simbólico, junction ou reparse point. Paths de leitura/validação são relativos à raiz e devem existir. Destinos de escrita protegida podem ser novos diretórios filhos, mas cada componente já existente é revalidado e os arquivos são criados com semântica sem overwrite.

O scanner/extrator existente do Core continua sendo a fonte dos dados. Ele mantém exclusões obrigatórias de arquivos sensíveis e leituras limitadas. `.env`, nomes de credenciais/chaves e outros paths protegidos não são expostos. Corpos de código-fonte, instruções de README, connection strings e secrets brutos não entram nos payloads MCP de evidência.

Essas verificações reduzem escapes acidentais do checkout aprovado, mas não prometem garantias atômicas contra alterações maliciosas concorrentes no filesystem. Execute o Repo2C4 sobre checkout confiável, estável, preferencialmente somente leitura e com privilégio mínimo.

## Limite arquitetural

O projeto MCP referencia o Core; o Core não referencia o SDK MCP. Os contratos v1 `RepositorySnapshot` e `Evidence` continuam sendo a fonte de verdade.

O servidor não faz inferência arquitetural. Evidência estática, hipótese e fato arquitetural confirmado continuam distintos. `ProjectReference`, referências de pacotes e categorias terminadas em `.candidate` continuam sendo apenas sinais estáticos e não se tornam relações runtime confirmadas por serem expostas via MCP. A interpretação arquitetural continua sendo responsabilidade do cliente MCP.

A issue #15 reutiliza emissor e validador do Core sem adicionar IA, operações Git, push/PR automático ou edição semântica de documentação existente. A #16 adiciona configuração genérica de cliente, prompts reutilizáveis e reprodução determinística C1/C2 com cliente de protocolo. Consulte [fluxo com cliente MCP](mcp-client.pt-BR.md).


## C3 seletivo

A issue #18 mantém o modelo C1/C2 estável e adiciona uma extensão C3 compatível, limitada a um único container C2 explicitamente selecionado. `generate_likec4` aceita o parâmetro opcional `c3ContainerId`; sem ele, o comportamento C1/C2 permanece inalterado. Uma seleção válida deriva uma proposta limitada de componentes somente das evidências já associadas ao container selecionado e inclui os componentes dentro do container selecionado em `model.c4` e gera `c3.views.c4`. Outros containers não recebem C3 automaticamente. Evidências estáticas ou candidatas permanecem `requiresReview`; container inexistente, evidência insuficiente, schema incompatível e limites excedidos geram erros controlados.


## Regeneração gerenciada e revisão

A issue #19 torna a regeneração review-first. `generate_likec4` continua em dry-run por padrão. Quando `destinationPath` é informado, o dry-run compara a proposta com o manifesto local e os arquivos atuais e devolve as mudanças sem tocar no filesystem. A aplicação deve ocorrer somente após revisar mudanças de fronteira e evidências. A escrita só é permitida quando cada arquivo gerenciado existente ainda corresponde ao SHA-256 registrado. Edição humana, arquivo gerenciado removido, arquivo linkado ou colisão com arquivo não gerenciado gera conflito e permanece intocado. Arquivos desconhecidos nunca são excluídos.
