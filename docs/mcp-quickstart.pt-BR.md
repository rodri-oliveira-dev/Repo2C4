# Quickstart de clientes MCP

[English](mcp-quickstart.md)

O Repo2C4 é um servidor MCP local por **stdio**. O host inicia `repo2c4-mcp`; o Repo2C4 não incorpora nem exige SDK proprietário de cliente.

## Instalar

```bash
dotnet tool install --global Repo2C4.Mcp --version 1.0.0
npm install --global likec4@1.59.4
repo2c4-mcp --help
likec4 --version
```

Autorize somente uma raiz absoluta existente. Não use diretório pessoal, raiz do disco ou pasta contendo repositórios não relacionados. Se a CLI também estiver instalada, `repo2c4 init --repository /caminho/absoluto` e `repo2c4 doctor --repository /caminho/absoluto` preparam/verificam a mesma raiz sem iniciar análise ou inferência.

## Conectar

Copie um exemplo de [`examples/mcp-clients/`](../examples/mcp-clients/).

**Stdio portátil:** use [`generic.mcp.json`](../examples/mcp-clients/generic.mcp.json) em host compatível e substitua o placeholder por caminho local absoluto.

**Visual Studio Code:** copie [`vscode.mcp.json`](../examples/mcp-clients/vscode.mcp.json) para `.vscode/mcp.json`. O exemplo usa `${workspaceFolder}` como raiz autorizada. Inicie/reinicie com **MCP: List Servers** ou pelas ações do editor MCP e confirme as ferramentas no Chat. O VS Code controla confiança, ciclo de vida e eventual sandbox; o Repo2C4 aplica sua própria fronteira. Em sessão remota, servidor e caminho são resolvidos no ambiente onde a configuração roda.

**Claude Desktop:** [`claude-desktop.json`](../examples/mcp-clients/claude-desktop.json) mostra a entrada stdio local. Substitua o placeholder absoluto e mescle somente `repo2c4` à configuração MCP local existente. Reinicie o Claude Desktop e confira servidor/ferramentas nas superfícies de conectores/desenvolvedor. O Claude Desktop hoje prioriza Desktop Extensions para integrações locais empacotadas; esta issue não cria extensão proprietária. Conectores remotos web/mobile não substituem este fluxo de filesystem local.

## Fluxo evidence-first

1. Chame `inspect_repository` com ID estável.
2. Pagine `get_evidence`; use `get_snapshot` quando necessário.
3. Proponha C1/C2 somente com IDs de evidência retornados e mantenha hipóteses sob revisão.
4. Chame `generate_likec4` sem escrita primeiro, como preview/dry-run.
5. Use `validate_likec4` em workspace gerado existente quando aplicável.
6. Após revisar o preview, chame `generate_likec4` com autorização explícita de escrita e destino relativo dentro da raiz.
7. Valide o workspace gravado e revise `get_evidence_report`.

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
| Conflito de escrita | Faça preview primeiro. O Repo2C4 bloqueia colisões e arquivos gerenciados alterados. |

Nunca coloque API key, token, conteúdo de repositório privado ou caminho pessoal nesses arquivos.
