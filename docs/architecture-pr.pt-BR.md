# Workflow manual de PR de documentação LikeC4 (Fase 5, issue #22)

O workflow [Reviewable LikeC4 documentation PR](../.github/workflows/architecture-pr.yml) é executado **somente sob acionamento manual** (`workflow_dispatch`) a partir da branch protegida `main`. Não processa PRs, forks, referências arbitrárias nem repositórios externos. O checkout utiliza o SHA selecionado em `main`, sem persistir credenciais. O código compilado é o do Repo2C4 nessa referência confiável; o diretório escolhido para inspeção não é executado.

**A execução manual do novo workflow só estará disponível após o merge da Fase 5 na branch padrão.** Os testes do script e a validação real com LikeC4 são executados no CI da própria branch da fase, sem publicação.

## Entradas

No GitHub, acesse **Actions → Reviewable LikeC4 documentation PR → Run workflow**, selecione `main` e preencha:

| Entrada | Descrição |
| --- | --- |
| `repository` | `owner/repo` exato do repositório atual. Outros repositórios e forks são recusados. |
| `source_ref` | Somente `main`, com checkout fixado no SHA validado. |
| `repository_root` | Raiz .NET relativa ao checkout, ou `.` para todo o repositório. Não aceita caminhos absolutos, travessia ou links simbólicos. |
| `mode` | `reviewed` para um modelo JSON já revisado por uma pessoa; `infer` para proposta de IA com revisão obrigatória. |
| `model_path` | Exigido somente em `reviewed`: caminho relativo para um `ArchitectureModel` v1 cujo snapshot corresponda exatamente à nova inspeção de `repository_root`. |
| `provider`, `model_id` | Em `infer`, use `openai` e informe um ID de modelo suportado; deixe vazios em `reviewed`. Ollama local não está disponível no runner hospedado. |
| `allow_external_ai` | Consentimento explícito obrigatório apenas para `infer`. |
| `output_id` | Identificador ASCII minúsculo com até 40 caracteres, destino fixo `docs/generated/<output_id>/`. |

Exemplo reproduzível: `repository_root=examples/fixtures/library-only`, `mode=reviewed`, `model_path=examples/end-to-end/architecture.c1.v1.json` e `output_id=library-example`.

## Segurança, validação e permissões

Para inferência cloud, configure `OPENAI_API_KEY` em **Settings → Secrets and variables → Actions → New repository secret**. A chave é disponibilizada somente na etapa de inferência com consentimento explícito. Sem chave, consentimento, provedor ou modelo válidos, não ocorre chamada externa. Apenas metadados sanitizados e limitados são enviados ao endpoint HTTPS fixo da OpenAI. O uso da API pode gerar cobrança. Consulte a [documentação de custo e confidencialidade](inference-openai.pt-BR.md).

O job `prepare`, com permissão somente de leitura, executa restore locked, formatação, build Release, testes, inspeção, verificação do snapshot revisado (quando aplicável), geração protegida, validação oficial LikeC4 e diff. O snapshot original e o candidato de IA não são incluídos no artefato; apenas arquivos `.c4`, `evidence-report.md` e manifesto gerenciado aprovados são transferidos. Se não houver alterações, nenhum PR é aberto.

O job `publish` é o único com `contents: write`, `pull-requests: write` e `actions: read`. Ele confirma identidade e checksums do artefato e SHA de `main`, cria branch dedicada `bot/repo2c4-<output_id>` e abre um PR para `main`. PR já aberto para a mesma branch é identificado sem duplicação; branch órfã gera erro para análise manual, sem `force push`. O PR lista arquivos, validações e link para `evidence-report.md`, enfatizando que hipóteses precisam de revisão humana. Não há aprovação ou merge automático, nem push direto em `main`.

A publicação exige que o repositório permita token de workflow com escrita e criação de pull requests em **Settings → Actions → General → Workflow permissions**, respeitando as regras de branch. Caso contrário, o job apresenta diagnóstico de permissão; não substitua a política por credenciais permanentes. PR criado com `GITHUB_TOKEN` pode não disparar automaticamente outros workflows de PR; confirme manualmente os checks exigidos antes de qualquer merge.

## Testes

O CI executa testes de fixture com ferramentas externas simuladas para os casos: ausência de diff, falha de schema/LikeC4, credencial ausente, consentimento obrigatório, raiz e branch não autorizadas, snapshot divergente, escrita negada e diff válido com abertura prevista de um único PR. Também executa a CLI real e o LikeC4 oficial sobre a fixture versionada e valida os arquivos gerados. O workflow passa por `actionlint` pinado. Esses testes não enviam dados a provedores cloud nem criam PRs reais.
