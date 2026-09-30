# Repo2C4 Agent

O Repo2C4 Agent é o agente local de documentação arquitetural distribuído como .NET Tool `Repo2C4.Agent`, comando `repo2c4-agent`. Ele usa Microsoft Agent Framework para orquestração de modelo/tools e atua estritamente como **cliente do servidor stdio independente `Repo2C4.Mcp`**. O Agent não referencia Core, CLI ou MCP como bibliotecas de domínio arquitetural.

O fluxo esperado é:

```text
objetivo explícito
  -> Agent Framework
  -> tools de inspeção/evidência do Repo2C4 MCP
  -> proposta C1/C2 evidence-first
  -> relatório de evidências + preview LikeC4 dry-run controlados pelo host
  -> workflow limitado de validação/correção
  -> aprovação humana opcional via Agent Framework
  -> escrita protegida pelo MCP
  -> arquivos LikeC4 validados
```

Conteúdo do repositório, descrições de evidência, diagnósticos e resultados de tools são dados não confiáveis. Eles nunca autorizam escrita nem substituem a política do host.

## Instalação

Agent, MCP e CLI usam a mesma versão do produto:

```bash
dotnet tool install --global Repo2C4.Agent --version 1.0.0
dotnet tool install --global Repo2C4.Mcp --version 1.0.0
dotnet tool install --global Repo2C4.Cli --version 1.0.0
repo2c4-agent --help
```

O Agent exige .NET 10. O MCP precisa estar disponível como comando instalado `repo2c4-mcp` ou por caminho absoluto em `--mcp-server-path`. O LikeC4 continua sendo dependência independente para validação; o CI fixa `likec4@1.59.4`.

## Ollama: provider local

Ollama é o provider local. O Repo2C4 aceita somente uma origem HTTP loopback em `--endpoint`; o default é `http://127.0.0.1:11434/`.

```bash
repo2c4-agent \
  --provider ollama \
  --model SEU_MODELO_LOCAL \
  --repository-root "/caminho/absoluto/do/repositorio" \
  --goal "Documente a arquitetura C1 e C2 atual de forma conservadora."
```

Provider e modelo são sempre explícitos. O Repo2C4 não escolhe nem baixa modelo silenciosamente.

## OpenAI: consentimento externo explícito

OpenAI é um provider hospedado/externo. Use somente quando os metadados do repositório puderem sair do host:

```bash
export OPENAI_API_KEY="fornecida-pelo-seu-secret-store"

repo2c4-agent \
  --provider openai \
  --model SEU_MODELO_OPENAI \
  --allow-external-ai \
  --repository-root "/caminho/absoluto/do/repositorio" \
  --goal "Documente a arquitetura C1 e C2 atual de forma conservadora."
```

`--allow-external-ai` é obrigatório. A chave é lida somente do ambiente do processo do Agent, nunca é aceita como argumento e não é herdada pelo processo filho MCP. Logs estruturados não incluem prompt completo, evidência bruta, argumentos de tools nem secrets.

## Conexão MCP e independência

Sem `--mcp-server-path`, o Agent inicia o comando instalado `repo2c4-mcp` e fornece a raiz autorizada. Um caminho explícito pode apontar para executável instalado ou para um `Repo2C4.Mcp.dll` absoluto.

```bash
repo2c4-agent \
  --provider ollama \
  --model SEU_MODELO_LOCAL \
  --repository-root "/caminho/absoluto/do/repositorio" \
  --mcp-server-path "/caminho/absoluto/para/repo2c4-mcp" \
  --goal "Explique a arquitetura atual e as fronteiras ainda não resolvidas."
```

O MCP continua sendo produto independente. VS Code, Claude Desktop ou outro host MCP pode usar `repo2c4-mcp` diretamente sem instalar/executar `repo2c4-agent`. O Agent adiciona um cliente de orquestração opinativo; a inteligência do modelo não foi movida para o MCP.

## Análise, validação e correção limitada

Em cada execução, o Agent:

1. inicia por `inspect_repository`;
2. recupera páginas limitadas de snapshot/evidências necessárias;
3. envia C1 e C2 pelo `generate_likec4` forçado a preview;
4. deixa o host executar `get_evidence_report`;
5. deixa o host executar `validate_likec4`;
6. em falha de validação, envia somente diagnósticos sanitizados para uma tentativa limitada de correção;
7. retorna resultado terminal estruturado.

Os status públicos são `completed`, `requires_review`, `validation_failed`, `cancelled` e `failed`. Evidência insuficiente aparece como `failed` com motivo terminal `insufficient_evidence`; estouros de budget usam códigos explícitos.

Um workspace LikeC4 válido **não** prova que as afirmações arquiteturais são verdadeiras. Qualidade da evidência e estado de revisão continuam visíveis.

## Aprovação humana e escrita

Sem `--write-destination`, o Agent não pede escrita.

Para permitir que uma proposta validada fique elegível a escrita local:

```bash
repo2c4-agent \
  --provider ollama \
  --model SEU_MODELO_LOCAL \
  --repository-root "/caminho/absoluto/do/repositorio" \
  --goal "Documente C1/C2 e prepare uma atualização local para aprovação." \
  --write-destination "docs/architecture"
```

Depois da validação, o host prepara um preview imutável específico do destino e o Microsoft Agent Framework emite uma solicitação `ApprovalRequiredAIFunction`. O console mostra destino, arquivos/alterações e IDs `requiresReview`. Somente aprovação local explícita pode chamar a escrita protegida. Texto do repositório, saída do modelo e prompts nunca aprovam a operação.

O gateway MCP revalida freshness do preview e conflitos de arquivos gerenciados imediatamente antes da aplicação. Preview stale ou arquivo humano alterado aborta a escrita em vez de sobrescrever.

## Limites operacionais

Defaults seguros são configuráveis somente dentro de faixas limitadas:

| Limite | Opção | Default | Faixa |
| --- | --- | ---: | ---: |
| Duração total | `--max-duration-seconds` | 300 s | 1–1800 |
| Timeout por request do provider | `--timeout-seconds` | 90 s | 1–300 |
| Tool calls | `--max-tool-calls` | 40 | 1–100 |
| Iterações do workflow | `--max-workflow-iterations` | 3 | 1–3 |
| Tentativas de validação | `--max-validation-attempts` | 3 | 1–3 |
| Páginas de evidência | `--max-evidence-pages` | 20 | 1–50 |
| Resposta acumulada | `--max-response-chars` | 32000 | 1024–100000 |
| Contexto acumulado | `--max-context-chars` | 64000 | 4096–200000 |

Cancelamento propaga do host para Agent Framework, workflow e MCP. Cada execução tem run ID, contadores e logs estruturados sanitizados. A instrumentação OpenTelemetry é opcional; operação normal não exige collector/exporter.

## Exemplo reproduzível com fixture

Em um checkout confiável do Repo2C4, instale as três tools e o LikeC4 e execute o Agent contra a fixture `library-only`:

```bash
npm install --global likec4@1.59.4

repo2c4-agent \
  --provider ollama \
  --model SEU_MODELO_LOCAL \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --goal "Use apenas evidências do Repo2C4 MCP. Produza documentação C1/C2 conservadora; mantenha fronteiras sem suporte como requiresReview."
```

Com essa fixture espera-se:

- MCP comprova um projeto .NET do tipo library e seu target framework;
- a ausência de host executável impede confirmar automaticamente fronteira de deployment/container;
- hipóteses úteis permanecem `requiresReview`;
- geração continua preview-only sem `--write-destination`;
- com destino de escrita, o prompt humano aparece somente **depois** da validação.

Há um walkthrough copiável em [examples/agent/README.md](../examples/agent/README.md).

## Troubleshooting

**`provide --repository-root as an existing absolute local directory`**  
Use diretório absoluto existente. O Agent rejeita diretório atual implícito.

**`the local Repo2C4 MCP server could not be started or initialized`**  
Instale `Repo2C4.Mcp`, confirme `repo2c4-mcp` no `PATH` ou passe `--mcp-server-path` absoluto.

**Ollama falha/timeout**  
Confirme que Ollama está ouvindo em loopback, que o modelo existe e que o timeout é suficiente. Endpoint Ollama fora de loopback é rejeitado.

**OpenAI falha antes da análise**  
Forneça `OPENAI_API_KEY` pelo ambiente e inclua `--allow-external-ai`. Não coloque a chave em histórico, arquivo ou argumento.

**`validation_failed`**  
Execute LikeC4 localmente se precisar de diagnóstico DSL mais profundo. O Agent entrega ao ciclo de correção apenas diagnósticos limitados/sanitizados.

**`insufficient_evidence`**  
É resultado controlado, não autorização para inventar arquitetura. Adicione evidência melhor ou faça revisão arquitetural humana.

**conflito/stale preview na escrita**  
Preserve a edição humana, resolva o destino, execute novamente análise/preview/validação e aprove o novo plano imutável.

## Testes e smoke de provider

O CI obrigatório usa `IChatClient` scripted e processo MCP real, sem credencial cloud. Consulte [testes determinísticos do Agent](agent-testing.md). O smoke de provider real é somente manual e nunca roda como gate comum de push/PR.
