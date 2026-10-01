# Exemplo end-to-end do Repo2C4 Agent

Este exemplo usa a fixture versionada `library-only` para demonstrar o fluxo de segurança do Agent sem esconder a fronteira de revisão humana.

## 1. Instalar as ferramentas

```bash
dotnet tool install --global Repo2C4.Agent
dotnet tool install --global Repo2C4.Mcp
dotnet tool install --global Repo2C4.Cli
npm install --global likec4@1.59.4
```

Execute a partir do checkout do Repo2C4 para que o caminho da fixture exista. Confirme que o Ollama está em execução e que o modelo selecionado está instalado:

```bash
ollama list
# Se necessário:
ollama pull SEU_MODELO_LOCAL
```

## 2. Somente análise

Com Ollama rodando localmente:

```bash
repo2c4-agent \
  --provider ollama \
  --model SEU_MODELO_LOCAL \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --goal "Inspecione apenas via Repo2C4 MCP. Produza documentação C1/C2 conservadora e mantenha fronteiras sem suporte como requiresReview."
```

O Agent inicia `repo2c4-mcp` por stdio, pede evidências ao MCP, propõe C1/C2 por geração somente preview, obtém relatórios de evidência, valida LikeC4 e executa no máximo as tentativas limitadas de correção configuradas.

Nesta fixture, um modelo não deve promover fronteira de runtime/deployment para `confirmed`: o repositório contém somente um projeto library.

Nenhum arquivo `.c4` gerenciado é criado nesta etapa. Uma execução bem-sucedida imprime `Status: completed` ou `Status: requires_review`, seguida pelo run ID e pelos contadores limitados da execução.

### C3 seletivo opcional

Se uma proposta C2 revisada contiver um container com ID arquitetural `container_api`, autorize somente esse container:

```bash
repo2c4-agent \
  --provider ollama \
  --model SEU_MODELO_LOCAL \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --c3-container "container_api" \
  --goal "Produza C1/C2 e proponha C3 somente para container_api quando houver evidência suficiente."
```

Não derive o ID do nome de um projeto. Ele precisa identificar previamente um container C2; caso contrário, o C3 é omitido.

## 3. Escrita protegida opcional

Escolha explicitamente um destino relativo ao repositório:

```bash
repo2c4-agent \
  --provider ollama \
  --model SEU_MODELO_LOCAL \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --goal "Prepare documentação C1/C2 conservadora para revisão local." \
  --write-destination "docs/generated"
```

Com `--write-destination "docs/generated"`, os workspaces C1 e C2 aprovados são gravados em `docs/generated/c1` e `docs/generated/c2`.

A sequência esperada de controles é:

```text
objetivo
  -> evidências MCP
  -> proposta
  -> relatório de evidências
  -> preview LikeC4 dry-run
  -> validação
  -> solicitação de aprovação do Agent Framework
  -> decisão humana local
  -> escrita protegida pelo MCP
```

Antes do prompt de aprovação, nenhum arquivo arquitetural gerenciado é escrito. Responder não/cancel não grava nada. Responder sim permite somente o plano imutável e validado exibido; preview stale ou conflito com edição humana aborta em vez de sobrescrever.

## 4. Inspecionar o resultado

Depois de uma aprovação aplicada com sucesso:

```bash
repo2c4 validate --output examples/fixtures/library-only/docs/generated/c1
repo2c4 validate --output examples/fixtures/library-only/docs/generated/c2
```

Revise os `evidence-report.md` gerados e todos os itens `requiresReview` antes de tratar a documentação como arquitetura aceita.

## Variante OpenAI

Use provider hospedado somente com consentimento explícito e secret fornecido pelo ambiente:

```bash
export OPENAI_API_KEY="fornecida-pelo-seu-secret-store"

repo2c4-agent \
  --provider openai \
  --model SEU_MODELO_OPENAI \
  --allow-external-ai \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --goal "Produza documentação C1/C2 conservadora a partir das evidências do Repo2C4 MCP."
```

Não coloque a API key em argumentos, arquivos do repositório ou histórico do shell. O processo filho MCP não herda a credencial.

Consulte a [documentação do Agent](../../docs/agent.pt-BR.md) para limites, cancelamento, observabilidade e troubleshooting.
