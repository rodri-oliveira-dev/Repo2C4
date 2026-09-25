# Usando o Repo2C4 a partir de um cliente MCP

Na Fase 3, o Repo2C4 funciona como servidor MCP local por stdio. O servidor coleta evidências limitadas do repositório, recebe um `ArchitectureModel` C1/C2 proposto pelo cliente, faz preview determinístico do LikeC4, valida com a CLI oficial do LikeC4 e só grava após autorização explícita.

O servidor MCP não contém SDK de IA, credenciais de provedor nem seletor de modelo. Quando uma IA interpreta as evidências, o modelo é escolhido e executado pelo cliente MCP.

## Pré-requisitos

Compile o Repo2C4:

```bash
dotnet restore Repo2C4.slnx --locked-mode
dotnet build Repo2C4.slnx --configuration Release --no-restore
```

Para `validate_likec4`, instale o runtime de validação documentado. O CI usa Node.js `22.23.3` e `likec4@1.59.4`:

```bash
npm install --global --no-audit --no-fund likec4@1.59.4
likec4 --version
```

Inspeção, recuperação de evidências, preview e escrita protegida não exigem conta cloud nem credencial de IA.

## Configuração stdio genérica

Clientes MCP usam arquivos e telas de configuração diferentes. Configure um servidor stdio local com o equivalente ao formato abaixo, substituindo todos os placeholders por caminhos locais absolutos:

```json
{
  "mcpServers": {
    "repo2c4": {
      "command": "dotnet",
      "args": [
        "/CAMINHO/ABSOLUTO/DO/Repo2C4.Mcp.dll",
        "--repository-root",
        "/CAMINHO/ABSOLUTO/DO/REPOSITORIO/AUTORIZADO"
      ]
    }
  }
}
```

O executável compilado normalmente está em:

```text
src/Repo2C4.Mcp/bin/Release/net10.0/Repo2C4.Mcp.dll
```

A raiz configurada é a fronteira de segurança. Paths recebidos pelas ferramentas e destinos de escrita são relativos a essa raiz. Não configure um diretório mais amplo apenas por conveniência.

Também é possível definir `REPO2C4_REPOSITORY_ROOT` e omitir `--repository-root`; a opção explícita de linha de comando tem precedência.

## Instrução reutilizável para o cliente

Há uma instrução reutilizável versionada em:

- `examples/mcp-client/architecture-review-prompt.pt-BR.md`
- versão em inglês: `examples/mcp-client/architecture-review-prompt.md`

Ela orienta o cliente a:

1. chamar `inspect_repository`;
2. paginar evidências com `get_evidence`/`get_snapshot` quando necessário;
3. separar evidência observada de hipótese arquitetural;
4. propor C1 e C2 como `ArchitectureModel` v1;
5. manter fronteiras/relações sem suporte como `requiresReview`;
6. chamar primeiro `generate_likec4` em dry-run;
7. chamar `validate_likec4`;
8. solicitar aprovação explícita do usuário antes de qualquer `write=true`.

O cliente não deve interpretar `ProjectReference`, presença de pacote ou categoria `.candidate` como prova de comunicação runtime ou de fronteira de deployment.

## Reprodução determinística sem IA

O repositório contém um cliente de teste de protocolo independente de fornecedor que executa o fluxo completo da Fase 3 sem modelo hospedado ou cliente MCP proprietário:

```bash
dotnet test tests/Repo2C4.Mcp.Tests/Repo2C4.Mcp.Tests.csproj \
  --configuration Release \
  --no-build
```

`McpClientEndToEndTests.ProtocolClientReproducesVersionedC1AndC2Flow`:

1. copia `examples/fixtures/library-only` para um checkout temporário isolado;
2. inicia o executável real `Repo2C4.Mcp` por stdio;
3. realiza o handshake MCP;
4. chama `inspect_repository`;
5. envia os modelos versionados `examples/end-to-end/architecture.c1.v1.json` e `architecture.c2.v1.json`;
6. faz preview dos dois modelos e confirma que nenhum arquivo foi gravado;
7. compara o preview com os goldens C1/C2 versionados;
8. grava C1 e C2 somente com `dryRun=false` e `write=true`;
9. compara cada `specification.c4`, `model.c4` e `views.c4` gravado com os goldens;
10. quando `REPO2C4_LIKEC4_INTEGRATION=1`, valida propostas e diretórios gravados pela CLI real do LikeC4.

O CI executa esse cliente de protocolo sem qualquer IA paga e depois repete a suíte após instalar a CLI LikeC4 pinada, com `REPO2C4_LIKEC4_INTEGRATION=1`.

## Sequência segura de escrita

O fluxo interativo esperado é:

```text
inspect_repository
  -> get_evidence / get_snapshot quando necessário
  -> cliente propõe ArchitectureModel C1/C2
  -> generate_likec4 (dryRun=true)
  -> validate_likec4
  -> usuário revisa preview e diagnósticos
  -> usuário aprova explicitamente um destino relativo
  -> generate_likec4 (dryRun=false, write=true)
  -> validate_likec4(destinationPath=...)
```

Pedir análise ou validação não autoriza escrita. Arquivos gerados já existentes nunca são sobrescritos pela ferramenta MCP.

## Limites de interpretação

As evidências do Repo2C4 são evidências do repositório, não observações de runtime. Um sinal em código/API/pacote pode sustentar uma hipótese sem confirmar deployment, ownership ou comunicação.

O servidor MCP valida o contrato v1 e algumas fronteiras de revisão, mas não decide que uma arquitetura proposta está semanticamente correta. Afirmações arquiteturais materiais continuam exigindo revisão humana.

A CLI da Fase 2 continua sendo um host offline de `inspect`/`generate`/`validate` que recebe um modelo já fornecido. Um futuro modo CLI direto assistido por IA, no qual a própria CLI seleciona/chama um provedor de interpretação, não faz parte da Fase 3.
