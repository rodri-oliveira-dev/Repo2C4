# Repo2C4

[English](README.md)

Repo2C4 é uma ferramenta .NET 10 para inspecionar repositórios .NET locais autorizados, coletar evidências arquiteturais rastreáveis e gerar documentação LikeC4 revisável. O projeto não converte hipóteses sem evidências em fatos confirmados.

## Componentes

| Projeto | Responsabilidade |
| --- | --- |
| `Repo2C4.Core` | Contratos versionados, inventário local seguro, extração de evidências e emissão LikeC4 determinística. Interno, sem publicação independente. |
| `Repo2C4.Cli` | Ferramenta `repo2c4` com `inspect`, `generate`, `validate` offline e `infer` opcional por Ollama local ou OpenAI com consentimento explícito. |
| `Repo2C4.Mcp` | Ferramenta `repo2c4-mcp`, servidor MCP local por stdio com raiz autorizada, inspeção e geração protegida. A escolha de IA é responsabilidade do cliente MCP. |

## Instalação reproduzível dos dois pacotes

É necessário o SDK .NET 10 e o validador oficial LikeC4 instalado separadamente. Em um checkout confiável:

```bash
dotnet tool restore
dotnet restore Repo2C4.slnx --locked-mode
dotnet build Repo2C4.slnx --configuration Release --no-restore
npm install --global likec4@1.59.4
bash scripts/verify-distribution.sh artifacts/distribution 1.0.0
```

O script empacota e instala a CLI e o MCP em um feed NuGet local isolado, testa os comandos instalados e a validação real LikeC4. Não publica os pacotes no NuGet.org, não cria release e não envia dados para a nuvem. As instruções de instalação no Windows, os comandos de instalação manual, os limites e a release opcional estão em [Distribuição e release verificável](docs/distribution.pt-BR.md).

## Exemplo: repositório até LikeC4

```bash
repo2c4 inspect --repository /caminho/absoluto/do/repositorio-dotnet --output snapshot.json
# Revise as evidências e produza architecture.reviewed.json; a inferência é opcional.
repo2c4 generate --model architecture.reviewed.json --output likec4
repo2c4 generate --model architecture.reviewed.json --output likec4 --apply
repo2c4 validate --output likec4
```

Para propor um `candidate.json` com Ollama local, execute `repo2c4 infer --snapshot snapshot.json --provider ollama --model-id MODELO_LOCAL --output candidate.json`. No modo cloud, selecione `--provider openai`, `--model-id`, `--allow-external-ai` e forneça `OPENAI_API_KEY` pelo ambiente. Os metadados sanitizados enviados à nuvem ainda podem revelar características da arquitetura e gerar cobranças. **Revise os elementos, relações e evidências antes de gerar a documentação**, preservando `requiresReview` onde não há confirmação. O [exemplo C1/C2 e C3 seletivo](examples/end-to-end/README.md) contém fixtures e modelos previamente revisados; uma biblioteca isolada não comprova execução de container.

## MCP e permissões

O servidor `repo2c4-mcp` usa exclusivamente stdio e exige `--repository-root /raiz/absoluta/autorizada`. O cliente MCP pode inspecionar evidências, propor um modelo C1/C2 e solicitar geração/validação com autorização de escrita explícita. O servidor não contém SDK de IA nem armazena credenciais do cliente. Consulte [configuração genérica](docs/distribution.pt-BR.md), [fluxo do cliente](docs/mcp-client.pt-BR.md), [políticas de acesso](docs/mcp.pt-BR.md) e [inferência local](docs/inference.pt-BR.md) / [OpenAI](docs/inference-openai.pt-BR.md).

## Validação, segurança e release

O [CI](.github/workflows/ci.yml) verifica restore locked, formatação, compilação, testes/cobertura, validação LikeC4, testes de CLI/MCP, empacotamento e instalação limpa dos dois produtos. As verificações de CodeQL, Dependency Review, auditoria e os controles de PR permanecem aplicáveis. O [workflow de release](.github/workflows/release.yml) é exclusivamente manual na `main` e executa somente dry-run por padrão. Uma GitHub Release com os dois pacotes requer habilitação e confirmação explícitas; o fluxo **não publica no NuGet.org**. O [workflow de documentação](docs/architecture-pr.pt-BR.md) cria somente PR revisável, sem merge automático.

Limitações: inicialmente repositórios .NET locais, C1/C2 e C3 para um container selecionado, sem aquisição de URL Git remota, sem editor/renderizador próprio, sem execução de código de terceiros e sem aprovação arquitetural automática. Snapshots, relatórios e modelos revisados podem conter caminhos e nomes confidenciais. A Fase 6 amplia o onboarding e os canais públicos de instalação.
