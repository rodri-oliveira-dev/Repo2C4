# Distribuição e release verificável do Repo2C4

O Repo2C4 versão 1.0.1 fornece três ferramentas .NET 10 separadas: `Repo2C4.Cli` (`repo2c4`), `Repo2C4.Mcp` (`repo2c4-mcp`) e `Repo2C4.Agent` (`repo2c4-agent`). O Core permanece como referência interna, sem pacote próprio. O Agent é cliente do executável MCP por stdio e não depende de Core/MCP como biblioteca de domínio. A versão compartilhada vem de `Directory.Build.props`. O LikeC4 é um validador externo, não é instalado pelo Repo2C4.

## Instalação pública e teste local

As releases publicadas disponibilizam as três ferramentas pelo NuGet.org. Instale a versão exata indicada na release:

```bash
dotnet tool install --global Repo2C4.Cli --version 1.0.1
dotnet tool install --global Repo2C4.Mcp --version 1.0.1
dotnet tool install --global Repo2C4.Agent --version 1.0.1
repo2c4 --help
repo2c4-mcp --help
repo2c4-agent --help
```

O LikeC4 continua sendo uma dependência independente para validação/renderização e deve ser instalado separadamente.

## Canais públicos de distribuição

A release usa canais diferentes para hospedagem dos artefatos e descoberta dos produtos:

| Produto | Distribuição do artefato | Descoberta |
| --- | --- | --- |
| `Repo2C4.Cli` | NuGet.org + GitHub Packages + GitHub Release | GitHub/NuGet |
| `Repo2C4.Mcp` | NuGet.org + GitHub Packages + GitHub Release | metadata no Official MCP Registry via `server.json` |
| `Repo2C4.Agent` | NuGet.org + GitHub Packages + GitHub Release | GitHub/NuGet |

O Agent não é publicado artificialmente como servidor MCP nem como pacote de um marketplace específico. Ele permanece uma .NET Tool local e um cliente MCP independente.

### Official MCP Registry

O repositório contém `server.json` versionado na raiz para o nome:

```text
io.github.rodri-oliveira-dev/repo2c4-mcp
```

Ele aponta para o pacote público `Repo2C4.Mcp` no NuGet, usa `dnx` como runtime hint do .NET 10, transporte stdio e declara `--repository-root` como argumento local obrigatório do tipo `filepath`. A aquisição remota não faz parte da execução padrão publicada no Registry.

A validação de ownership do pacote NuGet depende deste marcador exato no README empacotado:

```html
<!-- mcp-name: io.github.rodri-oliveira-dev/repo2c4-mcp -->
```

O marcador vem de `src/Repo2C4.Mcp/README.md`. O smoke de distribuição valida nome do Registry, vínculo package/version, argumento obrigatório da raiz e marcador de ownership antes da release.

O Official MCP Registry armazena metadata, não o pacote NuGet, então o pacote correspondente precisa estar público primeiro. Como a indexação do NuGet é assíncrona, a instalação pública não é usada como gate da release imutável. O workflow protegido de release valida e empacota uma única vez, cria a tag, publica o mesmo payload no NuGet.org e GitHub Packages em paralelo e cria a GitHub Release somente depois que ambos os registries concluírem com sucesso. Um workflow manual separado, `Publish Repo2C4 MCP Registry metadata`, verifica que a versão publicada de `Repo2C4.Mcp` já está publicamente consumível e só então publica a metadata correspondente no Registry.

A autenticação usa o fluxo oficial GitHub Actions OIDC. O job recebe somente `contents: read` e `id-token: write`; não exige PAT nem secret persistente específico do MCP Registry. O workflow baixa uma versão fixada do `mcp-publisher`, confere seu SHA-256, valida `server.json`, verifica se aquela versão exata já existe, publica apenas quando ausente e confirma a leitura da versão pela API do Registry.

Para validação local ou recuperação manual, o mantenedor ainda pode executar:

```bash
mcp-publisher validate server.json
mcp-publisher login github
mcp-publisher publish server.json
```

Uma versão imutável duplicada do pacote não deve ser republicada. Se a indexação do NuGet ainda não tiver terminado, o workflow do Registry pode falhar sem afetar a release já concluída; execute novamente esse workflow mais tarde para a mesma versão. Correções apenas de metadata em `server.json` são permitidas após a release quando necessárias para atender à validação do Registry, desde que o identificador e a versão do pacote continuem vinculados à release NuGet imutável já publicada.

Ao preparar uma release futura, atualize `Directory.Build.props` e os dois campos de versão de `server.json` para o mesmo SemVer exato antes de executar a verificação de distribuição/release.


Para manutenção ou verificação offline em um checkout confiável, instale o SDK .NET 10 indicado em `global.json`, Node.js e o LikeC4 oficial. O CI utiliza Node.js `22.23.3` e `likec4@1.59.4`:

```bash
dotnet tool restore
dotnet restore Repo2C4.slnx --locked-mode
dotnet build Repo2C4.slnx --configuration Release --no-restore
npm install --global likec4@1.59.4
bash scripts/verify-distribution.sh artifacts/distribution 1.0.1
```

O script cria os três pacotes de produto, confere identidade e versão, usa um feed NuGet local isolado com fontes externas desabilitadas e instala os três comandos. Ele testa `inspect → generate --apply → validate`, o protocolo MCP por stdio e o Agent empacotado contra a fixture `library-only` usando um fake compatível com Ollama em loopback mais o MCP real instalado. O fake não propõe arquitetura de propósito; o resultado esperado é `insufficient_evidence` controlado e zero escrita. Não cria tag, release, publicação NuGet nem chamada de IA. Os diretórios temporários são excluídos e os pacotes permanecem em `artifacts/distribution/`, ignorado pelo Git. No Windows, instale os pacotes com `dotnet tool install --tool-path` em diretórios separados, ajuste os caminhos e invoque `repo2c4.exe`, `repo2c4-mcp.exe` e `repo2c4-agent.exe`.

Os três pacotes anexados à GitHub Release correspondente são exatamente o payload validado enviado ao NuGet.org e incluem `SHA256SUMS`. Prefira instalar a versão exata documentada na release em vez de depender de uma versão mais recente implícita.

## Repositório .NET até C1/C2 e C3 seletivo

Autorize explicitamente a raiz local e gere um novo snapshot:

```bash
repo2c4 inspect --repository /caminho/absoluto/do/repositorio-dotnet --output ./snapshot.json
```

O exemplo `examples/fixtures/library-only` possui um snapshot versionado em `examples/end-to-end/snapshot.v1.json`; os modelos C1 e C2 no mesmo diretório representam **decisões revisadas por uma pessoa**, não uma inferência automática. Se desejar, faça uma proposta opcional com Ollama local:

```bash
repo2c4 infer --snapshot snapshot.json --provider ollama --model-id MODELO_LOCAL --output candidate.json
```

Para a OpenAI, forneça `OPENAI_API_KEY` por secret/variável do host, selecione `--provider openai --model-id MODELO_COMPATIVEL --allow-external-ai` e aceite conscientemente o custo e a saída de metadados arquiteturais sanitizados para a nuvem. O MCP não chama provedor de IA: o próprio cliente MCP escolhe o modelo. Consulte [inferência local](inference.pt-BR.md) e [custo/confidencialidade na nuvem](inference-openai.pt-BR.md).

**Revise `candidate.json` antes de gerar a documentação**: confira cada afirmação com as evidências originais, descarte atores, relações e limites de sistemas sem sustentação e mantenha `requiresReview` para hipóteses ainda não verificadas. Salve a versão revisada como `architecture.reviewed.json`:

```bash
repo2c4 generate --model architecture.reviewed.json --output ./likec4
repo2c4 generate --model architecture.reviewed.json --output ./likec4 --apply
repo2c4 validate --output ./likec4
```

O primeiro `generate` é somente preview. A escrita exige `--apply` e respeita o manifesto de arquivos gerenciados; edições manuais não são sobrescritas. Examine `likec4/evidence-report.md`. O C1/C2 é o fluxo padrão. Para expandir somente **um container C2 existente**, utilize `--c3-container ID_DO_CONTAINER` com modelo C2 já revisado ([exemplo C3](../examples/end-to-end/README.md)). Uma biblioteca .NET isolada não implica um container em execução. O `validate` verifica o DSL LikeC4 instalado, não a veracidade das decisões arquiteturais.

## Instalação MCP e acesso ao sistema de arquivos

Configure o MCP para stdio com comando instalado e uma raiz local absoluta e autorizada, por exemplo:

```json
{
  "mcpServers": {
    "repo2c4": {
      "command": "/caminho/absoluto/para/repo2c4-mcp",
      "args": ["--repository-root", "/caminho/absoluto/do/repositorio-dotnet"]
    }
  }
}
```

O servidor rejeita raízes inexistentes, links simbólicos/junções e acessos fora da raiz. `stdout` é exclusivo do protocolo MCP; diagnósticos ficam no `stderr`. O cliente inspeciona evidências limitadas e paginadas, propõe o modelo e solicita geração/validação com autorização explícita de escrita. O `repo2c4-agent` é um desses clientes MCP, adicionando workflow/HITL via Microsoft Agent Framework; o MCP continua utilizável independentemente por outros hosts. Consulte também o [guia do Agent](agent.pt-BR.md). Consulte [fluxo do cliente MCP](mcp-client.pt-BR.md) e [política de acesso](mcp.pt-BR.md). Não use um checkout mutável/não confiável exposto a trocas concorrentes de links.

## Release manual e validação

O workflow [Release](../.github/workflows/release.yml) roda **somente por `workflow_dispatch` na `main` confiável**. A versão SemVer informada precisa coincidir com as versões declaradas pelos três projetos de produto e por `server.json`. Toda execução faz restore, formatação, build, testes, instala o LikeC4 oficial pinado, empacota `Repo2C4.Cli`, `Repo2C4.Mcp` e `Repo2C4.Agent` e comprova instalação/execução limpa a partir do payload local da release. Com `publish_release=false`, que é o padrão, o workflow apenas valida e não cria tag nem publicação pública.

Para publicar deliberadamente, habilite `publish_release=true` e informe `publication_confirmation=PUBLISH`. Depois da validação, o workflow confirma novamente que a `main` ainda aponta para o SHA validado e cria a tag da release. O mesmo artefato validado é então consumido por dois jobs protegidos em paralelo: um autentica no NuGet.org por Trusted Publishing/OIDC e publica os três pacotes; o outro publica os mesmos pacotes no GitHub Packages usando o `GITHUB_TOKEN` com escopo restrito. Ambos conferem `SHA256SUMS` antes da publicação. A indexação pública do NuGet é propositalmente **fora** do gate pós-publicação da release.

A GitHub Release só é criada depois que ambos os registries de pacotes concluem com sucesso. Ela anexa os três arquivos `.nupkg` validados e o `SHA256SUMS` à tag já criada. Se uma etapa posterior falhar depois que o workflow criou a tag, o job compensatório de rollback remove apenas uma GitHub Release parcial, quando existir, e mantém a tag para que qualquer retry continue preso ao commit validado. Versões imutáveis já publicadas no NuGet.org e GitHub Packages nunca são removidas. Se um push de pacote concluir apenas parcialmente, use **Re-run failed jobs** na mesma execução para reutilizar o artefato validado original; não inicie uma nova release para a mesma versão parcialmente publicada.

A publicação no Official MCP Registry fica fora da fronteira imutável da release. Depois que `Repo2C4.Mcp` estiver publicamente consumível no NuGet.org, execute manualmente [Publish Repo2C4 MCP Registry metadata](../.github/workflows/publish-mcp-registry.yml) para a versão já lançada. Esse workflow valida a release/tag existente, valida a metadata atual do Registry, publica somente a entrada MCP e pode ser repetido de forma independente sem reconstruir nem republicar o pacote NuGet.

O CI regular testa empacotamento e instalação sem credencial cloud nem permissão de release. CodeQL, Dependency Review e auditoria de dependências continuam obrigatórios. O workflow de [PR revisável de documentação](architecture-pr.pt-BR.md) é um fluxo manual distinto, sem merge automático.

## Limitações conhecidas

A análise prioriza repositórios .NET locais e não cobre todas as linguagens. Opcionalmente, a CLI e o MCP com opt-in explícito do host podem adquirir repositórios Git HTTPS públicos; repositórios privados/autenticados continuam fora do escopo. A inspeção não executa o código do repositório; impõe limites de arquivos, tamanho, evidências e caminhos. C1/C2 e C3 seletivo são propostas revisáveis, não prova da topologia em execução. Snapshots, modelos e relatórios podem conter nomes de caminhos confidenciais; cuide de seu armazenamento e compartilhamento. A inferência OpenAI é opcional e transmite somente uma projeção sanitizada, ainda assim reveladora de características arquiteturais. O projeto não inclui editor/renderizador próprio, instalação automática do LikeC4, SaaS, aprovação ou merge automático. A publicação pública continua manual e protegida; push comum e pull request nunca publicam pacotes.
