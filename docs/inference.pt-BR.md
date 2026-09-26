# Inferência arquitetural local com Ollama (Fase 5, issue #20)

O comando `infer` é opcional e requer execução explícita. `inspect`, `generate`, `validate` e o servidor MCP continuam funcionando offline, sem Ollama ou credenciais de IA. A inferência apenas propõe um modelo versionado; ela não grava arquivos LikeC4 nem modifica o código-fonte.

## Exemplo local

Instale e inicie o [Ollama](https://ollama.com/) na mesma máquina e baixe um modelo capaz de responder em JSON. Substitua `IDENTIFICADOR_DO_MODELO` pelo ID exato de um modelo instalado.

```bash
ollama pull IDENTIFICADOR_DO_MODELO
ollama serve

dotnet build Repo2C4.slnx --configuration Release
CLI="dotnet src/Repo2C4.Cli/bin/Release/net10.0/Repo2C4.Cli.dll"

$CLI infer \
  --snapshot examples/end-to-end/snapshot.v1.json \
  --provider ollama \
  --model-id IDENTIFICADOR_DO_MODELO \
  --output artifacts/inference/candidate.json
```

As opções `--endpoint http://127.0.0.1:11434/` e `--timeout-seconds 90` são opcionais. O endpoint aceita exclusivamente origens HTTP loopback (`localhost`, `127.0.0.1` ou `[::1]`), com porta configurável. Servidores remotos, URLs com credenciais ou caminhos adicionais são recusados. O cliente HTTP da CLI não usa proxy nessa conexão local.

O arquivo de saída não pode existir previamente. O snapshot deve obedecer ao contrato v1 e ter até 4 MiB. A projeção enviada ao modelo permite até 256 arquivos e 512 evidências; a requisição HTTP tem limite de 96 KiB e a resposta, 256 KiB. Falhas de conexão, indisponibilidade do modelo, respostas inválidas, cancelamento e timeout não geram `candidate.json`.

Somente aliases de arquivos, IDs de evidências, descrições de categorias fixas e um ID opaco e hasheado do repositório são transmitidos ao Ollama. **Nomes/caminhos originais, conteúdo bruto, descrições livres, diagnósticos do scan, hashes e segredos não são enviados.** A sanitização restringe deliberadamente os detalhes que podem ser inferidos. Nenhum código do repositório é executado pelo comando.

## Revisão e geração

O arquivo `candidate.json` incorpora localmente o snapshot original para preservar a proveniência. Portanto, trate-o como potencialmente sensível e não o publique sem inspeção. Cada elemento e relação propostos possui `status: "requiresReview"`, mesmo se a IA tentar classificá-los como confirmados. Uma evidência citada não comprova, por si só, uma relação de execução, um container ou uma fronteira de sistema.

Revise a proposta contra o snapshot original e o código quando necessário. Elimine hipóteses sem sustentação ou mantenha-as explicitamente pendentes. Salve o resultado como `architecture.reviewed.json` e execute:

```bash
$CLI generate --model architecture.reviewed.json --output artifacts/inference/likec4
$CLI generate --model architecture.reviewed.json --output artifacts/inference/likec4 --apply
$CLI validate --output artifacts/inference/likec4
```

O `generate` realiza um preview antes de qualquer gravação. O `validate` exige a CLI oficial do LikeC4 instalada. Consulte a [documentação detalhada em inglês](inference.md) e a [CLI em português](cli.pt-BR.md).

## Testes

A suíte da CLI usa um transporte HTTP falso e uma fixture local, sem depender de servidor externo, conta ou token. Ela valida a proposta, a preservação local do snapshot, a sanitização do payload, as falhas de resposta, os limites, o timeout, o cancelamento e a proteção de arquivos existentes.
