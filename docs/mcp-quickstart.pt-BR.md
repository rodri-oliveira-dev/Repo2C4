# Quickstart de clientes MCP

[English](mcp-quickstart.md)

O Repo2C4 é um servidor MCP local por **stdio**. O host inicia `repo2c4-mcp`; o Repo2C4 não incorpora nem exige SDK proprietário de cliente.

## Instalar

Depois que o pacote público estiver disponível:

```bash
dotnet tool install --global Repo2C4.Mcp --version 1.0.1
npm install --global likec4@1.59.4
repo2c4-mcp --help
likec4 --version
```

LikeC4 é necessário para `validate_likec4`; inspeção e recuperação de evidências não exigem conta de IA nem credencial cloud.

Antes de uma publicação pública, compile um checkout confiável e configure o cliente para executar o DLL Release por `dotnet`.

Autorize somente uma raiz absoluta existente. Não use diretório pessoal, raiz do disco ou pasta contendo repositórios não relacionados. Se a CLI também estiver instalada, `repo2c4 init --repository /caminho/absoluto` e `repo2c4 doctor --repository /caminho/absoluto` preparam/verificam a mesma raiz sem iniciar análise ou inferência.

## Conectar

Copie um exemplo de [`examples/mcp-clients/`](../examples/mcp-clients/). Os exemplos versionados mantêm aquisição remota **desabilitada por padrão**; adicione `--allow-remote-acquisition` somente quando a inspeção Git remota pública for realmente necessária.

**Stdio portátil:** use [`generic.mcp.json`](../examples/mcp-clients/generic.mcp.json) em host compatível e substitua o placeholder por caminho local absoluto.

**Visual Studio Code:** copie [`vscode.mcp.json`](../examples/mcp-clients/vscode.mcp.json) para `.vscode/mcp.json`. O exemplo usa `${workspaceFolder}` como raiz autorizada. Inicie/reinicie com **MCP: List Servers** ou pelas ações do editor MCP e confirme as ferramentas no Chat. O VS Code controla confiança, ciclo de vida e eventual sandbox; o Repo2C4 aplica sua própria fronteira. Em sessão remota, servidor e caminho são resolvidos no ambiente onde a configuração roda.

**Claude Desktop:** [`claude-desktop.json`](../examples/mcp-clients/claude-desktop.json) mostra a entrada stdio local. Substitua o placeholder absoluto e mescle somente `repo2c4` à configuração MCP local existente. Reinicie o Claude Desktop e confira servidor/ferramentas nas superfícies de conectores/desenvolvedor. O Claude Desktop hoje prioriza Desktop Extensions para integrações locais empacotadas; esta issue não cria extensão proprietária. Conectores remotos web/mobile não substituem este fluxo de filesystem local.

## Primeira chamada segura

Depois que o cliente conectar, inspecione a própria raiz autorizada:

```json
{
  "repositoryPath": ".",
  "maxFiles": 1000
}
```

`inspect_repository` devolve `snapshotId`, `repositoryId`, metadados de expiração/contagem e resumos limitados de evidências/diagnósticos. `repositoryId` é retornado pela tool; ele **não** é uma entrada. Guarde `snapshotId` para as chamadas seguintes da mesma sessão stdio. Snapshots expiram após 30 minutos e não sobrevivem a reconexões.

## Fluxo evidence-first

1. Chame `inspect_repository` com `repositoryPath="."` ou um subdiretório relativo seguro.
2. Pagine `get_evidence`; use `get_snapshot` para metadados de arquivos/diagnósticos quando necessário.
3. Construa ou revise um `ArchitectureModel` v1 completo usando somente snapshot/evidências da sessão.
4. Chame `get_evidence_report` para evidenciar afirmações que exigem revisão.
5. Chame `generate_likec4` com o default `dryRun=true`; informe o `destinationPath` pretendido quando quiser um plano de mudanças específico do destino.
6. Chame `validate_likec4` **sem** `destinationPath` para validar a proposta em workspace temporário isolado.
7. Revise arquivos gerados, mudanças, afirmações `requiresReview` e diagnósticos de validação.
8. Somente após aprovação local explícita, chame `generate_likec4` com `dryRun=false`, `write=true` e exatamente o destino relativo aprovado.
9. Chame `validate_likec4` novamente com o `destinationPath` gravado.

Para C3 seletivo, passe o ID exato de um container C2 existente em `c3ContainerId` tanto na geração da proposta quanto na validação da proposta.

O host escolhe o modelo de IA. O MCP Repo2C4 não possui chave de provedor cloud e não transforma hipótese em fato.

## Smoke reproduzível

No primeiro teste, autorize o caminho absoluto de `examples/fixtures/library-only` em um checkout do Repo2C4. Confirme que `inspect_repository` retorna somente evidências da fixture. Faça preview antes da escrita. Para comparação determinística, use modelos e saídas em `examples/end-to-end/`.

Clientes proprietários não são iniciados pelo CI. O CI valida JSON e invariantes de segurança; conexão/reload no aplicativo é o smoke manual documentado.

## Troubleshooting

| Sintoma | Verificação |
| --- | --- |
| Servidor/comando não encontrado | Confira `dotnet tool list --global`, PATH do cliente e `repo2c4-mcp --help`. |
| Caminho inválido | A raiz deve ser diretório absoluto existente e não pode ser symlink/junction/reparse point. |
| Acesso fora da raiz rejeitado | Intencional. Use a menor raiz necessária; não amplie para contornar a política. |
| LikeC4 indisponível | Instale LikeC4 separadamente e exponha `likec4` no PATH do processo MCP. |
| Cliente rejeita configuração | VS Code usa `servers`; formato portátil/Claude local usa `mcpServers`. |
| Ferramentas não aparecem | Reinicie/recarregue servidor ou cliente e consulte os logs MCP. |
| Snapshot sumiu após reconectar | Execute `inspect_repository` novamente; snapshots pertencem à sessão e expiram após 30 minutos. |
| `snapshot_mismatch` | Reconstrua/revise o modelo contra exatamente o snapshot da sessão atual. |
| Conflito de escrita | Preserve a edição humana, faça novo dry-run específico do destino e revise o novo plano. |
| Tool remota não aparece | Esperado sem `--allow-remote-acquisition` explícito. |

Nunca coloque API key, token, conteúdo de repositório privado ou caminho pessoal nesses arquivos.

## Repositório remoto público opcional

A aquisição remota fica intencionalmente ausente dos exemplos padrão. Inicie o servidor MCP com `--allow-remote-acquisition` somente quando necessário para expor explicitamente `inspect_remote_repository` para uma URL Git HTTPS pública e referência opcional. A aquisição remota fica desabilitada por padrão. Ele adquire o repositório em workspace temporário isolado, rejeita credenciais/submódulos/links, executa o mesmo scanner de evidências, mantém apenas o snapshot limitado na sessão MCP e remove o workspace. A ferramenta retorna a proveniência sanitizada da aquisição (URL, ref solicitado e commit resolvido) separadamente das evidências arquiteturais. O `inspect_repository` local continua sendo o padrão e não exige acesso à rede.
