# Inferência opcional na nuvem com OpenAI (Fase 5, issue #21)

O Repo2C4 oferece três fluxos distintos. No **MCP**, o cliente escolhe o modelo e decide como tratar evidências; o servidor MCP não contém provedor de IA, credencial nem chamadas externas de inferência. No **Ollama**, a CLI usa um provedor local opt-in por HTTP loopback, sem credencial de nuvem. No modo **OpenAI**, a CLI envia uma projeção sanitizada das evidências para `https://api.openai.com/v1/responses` somente quando o usuário informa **`--allow-external-ai`** e fornece `OPENAI_API_KEY` pelo ambiente do processo.

O uso da API pode gerar cobrança conforme o modelo escolhido, tokens de entrada/saída e as condições da conta. Antes de autorizar, consulte os [preços atuais da API](https://openai.com/api/pricing/) e as regras de confidencialidade da sua organização. Mesmo depois da sanitização, dados sobre tecnologias, categorias, quantidade de evidências, agrupamento anônimo por arquivo e referências repetidas **saem da sua máquina**. Sanitização não é garantia de anonimato. Consulte as [políticas de dados da API](https://platform.openai.com/docs/guides/your-data). A requisição define `store: false` para desabilitar o armazenamento opcional de respostas, sem substituir as demais regras de processamento, monitoramento ou retenção do provedor.

## Exemplo com consentimento explícito

Forneça a chave por um secret do host ou variável de ambiente segura. Não coloque credenciais em argumentos, código, histórico do shell, arquivos versionados, snapshot ou logs de CI. Escolha um ID de modelo compatível com a Responses API e retorno em modo JSON; não há lista de modelos fixa no aplicativo.

```bash
CLI="dotnet src/Repo2C4.Cli/bin/Release/net10.0/Repo2C4.Cli.dll"

$CLI infer \
  --snapshot examples/end-to-end/snapshot.v1.json \
  --provider openai \
  --model-id IDENTIFICADOR_COMPATIVEL \
  --allow-external-ai \
  --output artifacts/inference/cloud-candidate.json
```

Sem `--allow-external-ai`, o comando falha antes de ler credenciais ou enviar qualquer requisição. Sem `OPENAI_API_KEY`, a chamada externa também é recusada. `--endpoint` é exclusivo do Ollama local, não existe configuração de host externo arbitrário. `--timeout-seconds 90` pode definir timeout de 1 a 300 segundos. O adaptador da nuvem não segue redirecionamentos nem utiliza cookies ou proxies herdados.

Antes do envio, o stderr mostra o destino e a quantidade de **arquivos anonimizados** e **registros de evidência sanitizados** do snapshot, além dos metadados de provedor/modelo. A chave, os caminhos originais, descrições livres, prompt, respostas completas e corpo de erros HTTP não são registrados. Somente IDs opacos, aliases de arquivos, categorias factuais predefinidas e tipos de fonte são enviados. Não são enviados arquivos brutos, diagnósticos do scan, hashes ou descrições que possam conter segredos. A requisição tem limite de 96 KiB e a resposta, 256 KiB. Resposta incompleta, inválida ou não conforme ao contrato não cria `candidate.json`. Erros 401/403/429/5xx têm diagnósticos limitados; cancelamento pelo usuário é propagado.

O arquivo candidato incorpora o **snapshot original local**, portanto trate-o como potencialmente confidencial. Todas as afirmações propostas pela IA são classificadas como `requiresReview`, mesmo que o provedor tente confirmá-las. Revise e edite o candidato antes de executar os comandos `generate` (preview), `generate --apply` e `validate`. A IA não comprova relações de execução nem modifica automaticamente o repositório.

Os testes de CI utilizam HTTP simulado e fixtures locais, sem conta, credencial ou chamada real à nuvem. Para a alternativa local, consulte [Ollama](inference.pt-BR.md); para a seleção de IA pelo cliente, consulte [fluxo MCP](mcp-client.pt-BR.md).
