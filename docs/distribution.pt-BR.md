# Distribuição e release verificável do Repo2C4

O Repo2C4 versão 1.0.0 fornece duas ferramentas .NET 10 separadas: pacote `Repo2C4.Cli`, comando `repo2c4`, e pacote `Repo2C4.Mcp`, comando `repo2c4-mcp`. O Core permanece como referência interna, sem pacote próprio. A versão compartilhada vem de `Directory.Build.props`. O LikeC4 é um validador externo, não é instalado pelo Repo2C4.

## Instalação pública e teste local

As releases publicadas disponibilizam as duas ferramentas pelo NuGet.org. Instale a versão exata indicada na release:

```bash
dotnet tool install --global Repo2C4.Cli --version 1.0.0
dotnet tool install --global Repo2C4.Mcp --version 1.0.0
repo2c4 --help
repo2c4-mcp --help
```

O LikeC4 continua sendo uma dependência independente para validação/renderização e deve ser instalado separadamente.

Para manutenção ou verificação offline em um checkout confiável, instale o SDK .NET 10 indicado em `global.json`, Node.js e o LikeC4 oficial. O CI utiliza Node.js `22.23.3` e `likec4@1.59.4`:

```bash
dotnet tool restore
dotnet restore Repo2C4.slnx --locked-mode
dotnet build Repo2C4.slnx --configuration Release --no-restore
npm install --global likec4@1.59.4
bash scripts/verify-distribution.sh artifacts/distribution 1.0.0
```

O script cria apenas os dois pacotes de produto, confere identidade e versão, usa um feed NuGet local isolado com fontes externas desabilitadas, instala os dois comandos e testa `inspect → generate --apply → validate` e o comportamento do MCP por stdio. Não cria tag, release, publicação NuGet nem chamada de IA. Os diretórios temporários são excluídos e os pacotes permanecem em `artifacts/distribution/`, ignorado pelo Git. No Windows, instale os pacotes com `dotnet tool install --tool-path` em diretórios separados, ajuste os caminhos e invoque `repo2c4.exe` e `repo2c4-mcp.exe`.

Os pacotes anexados à GitHub Release correspondente são exatamente o payload validado enviado ao NuGet.org e incluem `SHA256SUMS`. Prefira instalar a versão exata documentada na release em vez de depender de uma versão mais recente implícita.

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

O servidor rejeita raízes inexistentes, links simbólicos/junções e acessos fora da raiz. `stdout` é exclusivo do protocolo MCP; diagnósticos ficam no `stderr`. O cliente inspeciona evidências limitadas e paginadas, propõe o modelo e solicita geração/validação com autorização explícita de escrita. Consulte [fluxo do cliente MCP](mcp-client.pt-BR.md) e [política de acesso](mcp.pt-BR.md). Não use um checkout mutável/não confiável exposto a trocas concorrentes de links.

## Release manual e validação

O workflow [Verify and optionally release Repo2C4 tools](../.github/workflows/release.yml) roda somente por `workflow_dispatch` na `main` confiável. A versão SemVer deve coincidir com a versão MSBuild já declarada. Sempre executa restore, formatação, build, testes, validação LikeC4, empacotamento e instalação isolada das duas ferramentas. **Por padrão, apenas valida: não cria tag, release, pacote público ou publicação NuGet.** Para publicar deliberadamente, habilite `publish_release=true` **e** digite `publication_confirmation=PUBLISH`. O job com escrita roda no environment protegido `release`, confirma que a `main` não mudou, valida os três artefatos esperados, cria/publica a GitHub Release versionada e envia somente `Repo2C4.Cli` e `Repo2C4.Mcp` ao NuGet.org usando `NUGET_API_KEY` fornecida pelo environment. Depois, um job somente leitura instala essa mesma versão diretamente do NuGet.org e executa os dois comandos como consumidor externo. Falhas de publicação ou propagação são reportadas sem expor a credencial.

O CI regular testa empacotamento e instalação sem credencial cloud nem permissão de release. CodeQL, Dependency Review e auditoria de dependências continuam obrigatórios. O workflow de [PR revisável de documentação](architecture-pr.pt-BR.md) é um fluxo manual distinto, sem merge automático.

## Limitações conhecidas

A análise inicial aceita repositórios .NET locais, não todas as linguagens nem URLs Git remotas. A inspeção não executa o código do repositório; impõe limites de arquivos, tamanho, evidências e caminhos. C1/C2 e C3 seletivo são propostas revisáveis, não prova da topologia em execução. Snapshots, modelos e relatórios podem conter nomes de caminhos confidenciais; cuide de seu armazenamento e compartilhamento. A inferência OpenAI é opcional e transmite somente uma projeção sanitizada, ainda assim reveladora de características arquiteturais. O projeto não inclui editor/renderizador próprio, instalação automática do LikeC4, SaaS, aprovação ou merge automático. A publicação pública continua manual e protegida; push comum e pull request nunca publicam pacotes.
