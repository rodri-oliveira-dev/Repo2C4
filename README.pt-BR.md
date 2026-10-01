# Repo2C4

[English](README.md)

Repo2C4 é uma ferramenta .NET 10 que transforma evidências limitadas e rastreáveis de um repositório .NET autorizado em **documentação de arquitetura C4 revisável em LikeC4**. O princípio é evidence-first: observações do repositório continuam sendo observações, interpretação arquitetural permanece explícita e hipóteses sem suporte nunca são promovidas automaticamente a fatos confirmados.

## O que é o Repo2C4?

O Repo2C4 trata um problema recorrente de documentação: diagramas arquiteturais ficam desatualizados quando repositório, interpretação arquitetural e documentação gerada evoluem separadamente.

O produto divide esse fluxo em etapas explícitas:

```text
repositório autorizado
    -> inspeção limitada
    -> snapshot versionado de evidências com proveniência
    -> revisão arquitetural humana/cliente
    -> ArchitectureModel versionado
    -> preview LikeC4 determinístico
    -> aplicação explícita
    -> validação oficial e visualização com LikeC4
```

A camada de evidências registra o que o Repo2C4 consegue observar com segurança nas declarações .NET suportadas. A camada de modelo registra decisões arquiteturais e hipóteses. **Afirmações `confirmed` devem referenciar IDs de evidência existentes no snapshot embutido.** Afirmações `requiresReview` podem referenciar evidências de apoio quando disponíveis, mas também podem permanecer intencionalmente sem IDs de evidência quando representam uma hipótese ainda sem suporte; nesse caso, o contrato exige um `reviewReason`. Uma pessoa — ou um cliente MCP operando sob revisão humana — decide o significado arquitetural das evidências.

O Repo2C4 mantém a orquestração de IA fora do Core e do MCP. O Core não faz chamadas de IA e o servidor MCP não contém SDK de provedor nem seletor de modelo. O `repo2c4-agent` opcional é um cliente separado, baseado em Microsoft Agent Framework, do `repo2c4-mcp`: ele orquestra análise evidence-first, validação/correção limitada e escrita com aprovação humana sem mover inteligência de modelo para o MCP. A CLI também preserva seus adaptadores simples de inferência opcional. Saída de IA nunca vira verdade arquitetural por si só.

## Capacidades e limitações atuais

### Disponível hoje

- **Inspeção local limitada:** examina uma única raiz local explicitamente autorizada, ignora caminhos sensíveis/gerados obrigatórios e escapes por links, aplica budgets de arquivos/bytes/entradas e não executa código do repositório.
- **Evidências .NET rastreáveis:** extrai declarações suportadas de solution/project/source com proveniência relativa ao repositório, mantendo declarações estáticas e candidatos de runtime semanticamente separados.
- **LikeC4 determinístico:** gera workspaces C1/C2 revisáveis e os valida com a CLI oficial do LikeC4 instalada separadamente.
- **C3 seletivo:** expande exatamente um container C2 selecionado explicitamente quando há evidência suficiente; os demais containers não recebem C3 automaticamente.
- **Escrita review-first:** `generate` é preview por padrão. Na CLI, escrita exige `--apply`; no MCP exige `dryRun=false`, `write=true` e um destino relativo autorizado. Hashes dos arquivos gerenciados protegem edições humanas.
- **CLI:** `repo2c4` oferece onboarding, inspeção, inferência opcional, geração e validação.
- **Servidor MCP stdio:** `repo2c4-mcp` expõe evidências, geração e validação dentro de uma raiz autorizada. A seleção de modelo, quando existir, pertence ao cliente MCP.
- **Agente de arquitetura:** `repo2c4-agent` usa Microsoft Agent Framework como cliente MCP independente, com provider/modelo explícitos, workflow/retries limitados, observabilidade sanitizada e HITL do Agent Framework antes de escrita protegida.
- **Aquisição Git pública opcional:** a CLI pode inspecionar um ref Git HTTPS público explicitamente selecionado em workspace temporário limitado. No MCP, aquisição remota só aparece quando o host habilita `--allow-remote-acquisition`.
- **Automação revisável:** o repositório inclui um workflow manual que pode propor alterações LikeC4 validadas via Pull Request sem merge automático.

### Limitações deliberadas

O Repo2C4 atualmente **não**:

- comprova comunicação em runtime, topologia de deployment, ownership ou fronteiras de container apenas com `ProjectReference`, pacotes, SDKs ou candidatos encontrados em código;
- avalia MSBuild, compila ou executa o repositório inspecionado, roda hooks/scripts do repositório ou publica corpos arbitrários de código-fonte no contrato de evidências;
- transforma automaticamente cada projeto .NET em container C4 nem gera C3 para todos os containers;
- fornece renderer/editor próprio — o workspace gerado é renderizado pelo LikeC4;
- suporta aquisição Git remota privada/autenticada, URLs SSH/file, submódulos ou análise que dependa de Git LFS;
- grava arquitetura gerada apenas por ter executado inspeção ou inferência;
- permite que o servidor MCP chame sozinho um provedor de IA hospedado;
- elimina a necessidade de revisão arquitetural. `validate` verifica sintaxe/integridade do workspace LikeC4, não se uma decisão arquitetural é verdadeira.

Para os limites precisos de segurança e evidência, consulte [contratos](docs/contracts.md), [CLI](docs/cli.pt-BR.md), [MCP](docs/mcp.pt-BR.md) e [distribuição/segurança](docs/distribution.pt-BR.md).

## Quick Start

O walkthrough abaixo usa a fixture versionada `library-only`, permitindo que um novo usuário reproduza as mesmas evidências e a mesma saída LikeC4. A fixture não contém host executável; essa ausência é proposital e demonstra que o Repo2C4 não inventa um container em runtime.

### 1. Clonar e instalar

```bash
git clone https://github.com/rodri-oliveira-dev/Repo2C4.git
cd Repo2C4

dotnet tool install --global Repo2C4.Cli --version 1.0.1
dotnet tool install --global Repo2C4.Mcp --version 1.0.1
dotnet tool install --global Repo2C4.Agent --version 1.0.1
npm install --global likec4@1.59.4
```

No seu próprio repositório, `repo2c4 init --repository /caminho/absoluto` cria somente a configuração local do Repo2C4 e `repo2c4 doctor --repository /caminho/absoluto` verifica pré-requisitos. Nenhum dos dois comandos inspeciona, compila, infere ou grava C4.

### 2. Inspecionar evidências

```bash
mkdir -p artifacts/quickstart

repo2c4 inspect \
  --repository examples/fixtures/library-only \
  --output artifacts/quickstart/snapshot.v1.json
```

O snapshot resultante é determinístico para essa fixture e pode ser comparado com [`examples/end-to-end/snapshot.v1.json`](examples/end-to-end/snapshot.v1.json).

### 3. Revisar o modelo e fazer preview do LikeC4

A inspeção termina nas evidências. O [modelo C1 versionado](examples/end-to-end/architecture.c1.v1.json) representa a etapa seguinte: uma proposta arquitetural deliberadamente revisada e vinculada ao snapshot.

```bash
repo2c4 generate \
  --model examples/end-to-end/architecture.c1.v1.json \
  --output artifacts/quickstart/c1
```

Esse comando é **somente preview**. Nenhum arquivo LikeC4 gerenciado é gravado ainda.

### 4. Aplicar explicitamente, validar e visualizar

Depois de revisar o preview, aplique a saída gerenciada de forma explícita:

```bash
repo2c4 generate \
  --model examples/end-to-end/architecture.c1.v1.json \
  --output artifacts/quickstart/c1 \
  --apply

repo2c4 validate --output artifacts/quickstart/c1
```

Para visualizar o workspace validado com a CLI do LikeC4 instalada separadamente:

```bash
cd artifacts/quickstart/c1
likec4 start
```

O LikeC4 serve as views geradas localmente. O Repo2C4 não fornece renderer próprio.

### 5. Conectar via MCP

Inicie o servidor stdio local com a menor raiz absoluta autorizada:

```bash
repo2c4-mcp --repository-root "/caminho/absoluto/para/Repo2C4/examples/fixtures/library-only"
```

Um cliente MCP deve chamar `inspect_repository` primeiro, recuperar evidências quando necessário, propor/revisar um modelo C1 ou C2, chamar `generate_likec4` no modo dry-run padrão, validar e pedir aprovação explícita antes de qualquer escrita. Configurações copiáveis para VS Code, Claude Desktop e stdio portátil estão no [quickstart MCP](docs/mcp-quickstart.pt-BR.md).

O fluxo de fixture da CLI é executado no [CI](.github/workflows/ci.yml), incluindo comparação determinística do snapshot, geração C1/C2, C3 seletivo, `--apply` explícito, proteção contra conflitos em arquivos gerenciados e validação oficial do LikeC4. O smoke de distribuição instala as três ferramentas .NET, exercita o protocolo MCP real por stdio e executa o Agent empacotado contra a fixture local usando um fake compatível com Ollama em loopback.

### 6. Executar o Agent (opcional)

Com Ollama rodando localmente e um modelo explicitamente selecionado:

```bash
repo2c4-agent \
  --provider ollama \
  --model SEU_MODELO_LOCAL \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --goal "Use apenas evidências do Repo2C4 MCP. Produza documentação C1/C2 conservadora."
```

O Agent é cliente do MCP, não substituto. Adicione `--write-destination docs/architecture` somente quando quiser que uma proposta validada chegue ao prompt de aprovação humana local antes de qualquer escrita. Consulte o [guia do Agent](docs/agent.pt-BR.md).

## Exemplo de saída: evidência → revisão → LikeC4

A entrada reproduzível é [`examples/fixtures/library-only/OnlyLib.csproj`](examples/fixtures/library-only/OnlyLib.csproj):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
```

A inspeção registra fatos com proveniência em vez de inventar arquitetura. O snapshot versionado contém, entre outras, esta evidência:

```json
{
  "id": "ev_1d5d5849e8163d2cd54b7d77",
  "category": "dotnet.project.kind",
  "relativePath": "OnlyLib.csproj",
  "line": 1,
  "description": "Project declaration indicates kind: Library."
}
```

A revisão humana então cria uma afirmação de modelo ligada a esses IDs de evidência. Como o repositório não contém sinal de executável/host, a fronteira do software system permanece explicitamente sob revisão:

```json
{
  "id": "el_library_repo",
  "kind": "softwareSystem",
  "name": "Library-only repository",
  "evidenceIds": [
    "ev_1d5d5849e8163d2cd54b7d77",
    "ev_cd4e2d70e36c097535a8395e"
  ],
  "status": "requiresReview"
}
```

A geração determinística preserva esse estado no LikeC4 em vez de confirmá-lo silenciosamente:

```likec4
model {
  el_library_repo = softwareSystem "Library-only repository" {
    #requires-review
    metadata {
      architectureId "el_library_repo"
      reviewStatus "requiresReview"
    }
  }
}
```

O snapshot completo, modelos C1/C2 revisados, arquivos gerados esperados, relatório de evidências e exemplo de C3 seletivo estão em [`examples/end-to-end/`](examples/end-to-end/README.md).

## Arquitetura e desenvolvimento

O Repo2C4 mantém os hosts de entrega separados do núcleo de evidências/geração:

| Projeto | Responsabilidade |
| --- | --- |
| `src/Repo2C4.Core` | Contratos versionados de evidência/modelo, inspeção limitada, extração de fatos .NET com evidências, política C1/C2 + C3 seletivo, emissão LikeC4 determinística, planejamento de saída gerenciada e adapters de validação. |
| `src/Repo2C4.Cli` | CLI para onboarding, inspeção local/remota pública, inferência opcional, preview/aplicação e validação. |
| `src/Repo2C4.Mcp` | Host MCP local por stdio com raiz explícita; ferramentas de evidência e LikeC4 por padrão e aquisição remota pública somente quando habilitada pelo host. |
| `src/Repo2C4.Agent` | Cliente independente do Repo2C4 MCP baseado em Microsoft Agent Framework; provider/modelo explícitos, orquestração evidence-first, workflow limitado, observabilidade e aprovação HITL. |
| `tests/Repo2C4.*.Tests` | Cobertura de contratos, fronteiras de segurança, geração determinística, CLI, Agent e protocolo MCP real. |

CLI e MCP referenciam o Core, nunca um ao outro. O Agent não referencia Core nem os projetos CLI/MCP como bibliotecas de domínio; conecta-se ao executável MCP por stdio. O Core não referencia os hosts.

### Inspeção, segurança e proveniência

O scanner trabalha em uma raiz autorizada, não segue symlinks/junctions/reparse points, exclui diretórios gerados e nomes sensíveis, rejeita conteúdo binário e aplica budgets padrão de **1.000 arquivos aceitos**, **1 MiB por arquivo**, **16 MiB no total** e **20.000 entradas visitadas**. A extração lê no máximo **512 KiB por arquivo aceito**, revalida contenção e links, rejeita XML com DTD/entidades externas e não inclui corpos de source, connection strings ou exceções no snapshot.

Categorias como `dotnet.project.reference` são declarações de build, e categorias terminadas em `.candidate` são apenas sinais candidatos. Elas não comprovam relações HTTP, broker, banco ou deployment. Consulte [contratos e categorias de evidência](docs/contracts.md).

### CLI, inferência e escrita gerenciada

`inspect`, `generate` e `validate` funcionam sem provedor de IA. A inferência é opcional: Ollama usa loopback local; OpenAI exige `--allow-external-ai`, `OPENAI_API_KEY` fornecida pelo host e envia somente uma projeção limitada/sanitizada. Toda afirmação proposta por IA continua `requiresReview`.

A primeira aplicação cria `.repo2c4-manifest.json` com hashes SHA-256 dos arquivos gerenciados. Uma aplicação posterior só altera arquivo cujo estado ainda corresponde ao manifesto; edição manual, remoção ou colisão não gerenciada bloqueiam a escrita. Consulte [CLI](docs/cli.pt-BR.md), [inferência local](docs/inference.pt-BR.md), [OpenAI/privacidade](docs/inference-openai.pt-BR.md) e [relatório de evidências](docs/evidence-report.md).

### MCP stdio

O servidor exige uma raiz local absoluta existente, reserva `stdout` exclusivamente para mensagens de protocolo e usa `stderr` para diagnósticos. As ferramentas padrão são `inspect_repository`, `get_evidence`, `get_snapshot`, `get_evidence_report`, `generate_likec4` e `validate_likec4`. `inspect_remote_repository` só é exposta com `--allow-remote-acquisition`.

Modelos enviados pelo cliente devem corresponder exatamente ao snapshot da sessão; evidência fabricada é rejeitada. Consulte [MCP](docs/mcp.pt-BR.md) e [fluxo do cliente](docs/mcp-client.pt-BR.md).

### Aquisição Git remota opcional

Paths locais continuam sendo a fonte principal e mais privada. Para um repositório Git HTTPS público:

```bash
repo2c4 inspect \
  --remote-url https://github.com/OWNER/REPOSITORY.git \
  --remote-ref refs/heads/main \
  --output snapshot.json
```

URL resolvida, ref solicitado e commit concreto são registrados separadamente em `snapshot.json.acquisition.json` como proveniência da aquisição, não como evidência arquitetural. A aquisição usa workspace temporário, nunca executa builds/hooks/scripts e remove o workspace ao final. Repositórios privados/autenticados, SSH/file, submódulos e Git LFS ficam fora do escopo.

### Validação e CI

O [CI](.github/workflows/ci.yml) verifica restore locked, políticas de build, formatação, compilação, testes/cobertura, LikeC4 oficial pinado, automação de documentação, ciclo offline da CLI, C3 seletivo, proteção de regeneração, empacotamento/instalação dos três .NET Tools e protocolo MCP real. CodeQL, Dependency Review e SonarQube Cloud complementam esses gates; consulte [SonarQube Cloud](docs/sonarqube-cloud.pt-BR.md).

### Distribuição e automação

As ferramentas públicas são `Repo2C4.Cli` (comando `repo2c4`), `Repo2C4.Mcp` (comando `repo2c4-mcp`) e `Repo2C4.Agent` (comando `repo2c4-agent`). O LikeC4 continua uma dependência independente. A release pública é manual/protegida e publica o payload validado no NuGet.org e GitHub Packages em paralelo, criando a GitHub Release somente depois que ambos os registries concluírem com sucesso. A indexação do NuGet é assíncrona e não é usada como gate pós-publicação da release. O `Repo2C4.Mcp` também possui metadata versionada para o Official MCP Registry em [`server.json`](server.json), publicada por um workflow manual separado somente depois que a mesma versão estiver publicamente consumível no NuGet. O `Repo2C4.Agent` permanece uma .NET Tool local/cliente MCP, sem acoplamento a marketplace específico. Consulte [distribuição](docs/distribution.pt-BR.md) e [workflow de PR arquitetural](docs/architecture-pr.pt-BR.md).
