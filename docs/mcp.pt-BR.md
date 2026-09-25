# Servidor MCP stdio, ferramentas de inspeção e política de acesso local

A Fase 3 executa o Repo2C4 como servidor MCP local por stdio. A issue #13 estabeleceu o transporte e o limite de filesystem; a issue #14 expõe somente inspeção limitada do repositório e recuperação de evidências. Ferramentas de geração/validação LikeC4 continuam fora do escopo até a issue #15.

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

## Erros controlados e limites

Descrições e schemas MCP informam parâmetros e limites. Erros controlados esperados incluem:

- `repository_path_invalid` para path fora da raiz, inexistente, traversal ou componente linkado;
- `repository_unavailable` quando o repositório não pode ser inspecionado com segurança;
- `max_files_invalid` e `page_size_invalid` para limites inválidos;
- `snapshot_not_found_or_expired` para snapshot ausente, expirado ou de outra sessão;
- `cursor_invalid` para cursor malformado, alterado, incompatível ou fora de faixa;
- `path_filter_invalid`, `category_invalid` e `section_invalid` para filtros inválidos;
- `tool_timeout` quando a inspeção excede 30 segundos;
- `response_limit_exceeded` quando a resposta estruturada ultrapassaria o orçamento MCP de 1 MiB.

O teto permanece em 1.000 arquivos inspecionados e 1 MiB por resposta MCP. Páginas de recuperação usam 50 itens por padrão e no máximo 100.

## Raiz autorizada do repositório

A raiz configurada deve ser um diretório absoluto existente, sem traversal, e não pode ser link simbólico, junction ou reparse point. Paths de repositório recebidos pelas ferramentas são relativos a essa raiz. A resolução rejeita paths absolutos, traversal, normalização para fora da raiz, paths inexistentes e componentes simbólicos/reparse.

O scanner/extrator existente do Core continua sendo a fonte dos dados. Ele mantém exclusões obrigatórias de arquivos sensíveis e leituras limitadas. `.env`, nomes de credenciais/chaves e outros paths protegidos não são expostos. Corpos de código-fonte, instruções de README, connection strings e secrets brutos não entram nos payloads MCP de evidência.

Essas verificações reduzem escapes acidentais do checkout aprovado, mas não prometem garantias atômicas contra alterações maliciosas concorrentes no filesystem. Execute o Repo2C4 sobre checkout confiável, estável, preferencialmente somente leitura e com privilégio mínimo.

## Limite arquitetural

O projeto MCP referencia o Core; o Core não referencia o SDK MCP. Os contratos v1 `RepositorySnapshot` e `Evidence` continuam sendo a fonte de verdade.

O servidor não faz inferência arquitetural. Evidência estática, hipótese e fato arquitetural confirmado continuam distintos. `ProjectReference`, referências de pacotes e categorias terminadas em `.candidate` continuam sendo apenas sinais estáticos e não se tornam relações runtime confirmadas por serem expostas via MCP. A interpretação arquitetural continua sendo responsabilidade do cliente MCP.

Geração, validação e escrita protegida de LikeC4 não foram adicionadas na issue #14 e permanecem reservadas à issue #15.
