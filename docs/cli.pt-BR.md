# CLI do Repo2C4

Os comandos offline `inspect`, `generate` e `validate` não chamam IA, abrem pull request, avaliam MSBuild nem transformam candidatos em arquitetura runtime confirmada. O comando opcional `infer` usa o Ollama local ou, com autorização explícita, o provedor cloud OpenAI para propor um modelo sujeito a revisão humana.

## Comandos

### Inspect

```bash
repo2c4 inspect --repository PATH --output snapshot.json
```

`inspect` inventaria um repositório local explicitamente selecionado e extrai apenas as evidências limitadas suportadas pelo Core. A saída é JSON v1 canônico.

O identificador do repositório é derivado de forma determinística do nome do diretório raiz selecionado. Ele é um rótulo local estável, não uma identidade globalmente única.

O arquivo de snapshot não pode existir previamente. Use outro caminho caso exista.

### Infer (IA local ou cloud autorizada)

```bash
repo2c4 infer --snapshot snapshot.json --provider ollama --model-id IDENTIFICADOR --output candidate.json
```

Para usar a OpenAI na nuvem, informe `--provider openai --allow-external-ai` e disponibilize `OPENAI_API_KEY` no ambiente do host. A CLI apresenta a quantidade de arquivos anonimizados e evidências sanitizadas antes do envio ao endpoint HTTPS fixo; sem consentimento ou chave nenhuma requisição externa é realizada. O uso pode gerar custo conforme modelo/tokens e os metadados transmitidos deixam a máquina. Consulte [consentimento, custo e confidencialidade](inference-openai.pt-BR.md).\n\nAs opções `--endpoint http://127.0.0.1:11434/` e `--timeout-seconds 90` configuram porta local e timeout. São aceitos somente endpoints HTTP loopback. O adaptador envia apenas uma projeção sanitizada e limitada das evidências, nunca arquivos brutos, segredos, caminhos originais ou descrições livres. A CLI valida o contrato v1, anexa localmente o snapshot original e obriga **revisão humana de todas as afirmações propostas pela IA**. Ela recusa respostas inválidas, modelo indisponível, timeout e sobrescrita do arquivo de saída. Veja [o tutorial de inferência local e revisão](inference.pt-BR.md).

### Generate

```bash
repo2c4 generate --model architecture.json --output DIR
```

A entrada deve ser um `ArchitectureModel` v1 válido, proposto ou revisado por uma pessoa. O comando não infere um modelo C4 a partir do snapshot.

São produzidos quatro arquivos dentro do diretório escolhido:

- `specification.c4`
- `model.c4`
- `views.c4`
- `evidence-report.md`

A geração é somente preview por padrão. O Repo2C4 informa `added`, `modified`, `unchanged` ou `conflict` para cada saída gerenciada e não altera o filesystem.

Para aplicar um preview já revisado:

```bash
repo2c4 generate --model architecture.json --output DIR --apply
```

A primeira aplicação bem-sucedida cria `.repo2c4-manifest.json`, contendo a versão de schema do modelo e o SHA-256 de cada saída gerenciada. Aplicações posteriores só são permitidas quando o arquivo atual ainda corresponde ao hash do manifesto. Arquivos editados manualmente, ausentes apesar de previamente gerenciados, linkados ou arquivos não gerenciados que colidem com uma saída são conflitos e permanecem intocados.

`evidence-report.md` relaciona as afirmações do modelo aos IDs de evidência e às localizações relativas do repositório, separa hipóteses pendentes, avisos de varredura e itens sem suporte, sem copiar corpos de código nem valores sensíveis. Consulte [relatório de evidências e revisão arquitetural](evidence-report.md).

Para solicitar C3 de exatamente um container C2 revisado, adicione `--c3-container ID`:

```bash
repo2c4 generate --model architecture.c2.json --output DIR --c3-container el_web
```

Sem essa opção nenhum arquivo C3 é gerado. Uma seleção válida aninha as propostas de componentes revisáveis dentro do container selecionado em `model.c4` e adiciona `c3.views.c4`; os demais containers C2 não recebem vistas de componentes automaticamente. A proposta C3 é limitada, mantém sinais estáticos/candidatos sob revisão e falha quando o container selecionado não possui evidência suficiente.

O diretório de saída e os arquivos gerenciados precisam permanecer dentro da raiz escolhida e não podem ser symlink, junction ou reparse point. Os nomes gerados são fixos pelo Repo2C4 e não podem ser definidos pelo conteúdo do modelo. As escritas são preparadas em um diretório transacional; o manifesto só é substituído após a preparação das saídas. Em caso de falha, arquivos previamente gerenciados são restaurados em best effort e arquivos desconhecidos nunca são excluídos.

### Validate

```bash
repo2c4 validate --output DIR
```

`validate` chama a CLI oficial do LikeC4 instalada no ambiente usando apenas o comando controlado `likec4 validate`. O Repo2C4 não instala Node.js nem LikeC4 em runtime. A baseline do CI usa LikeC4 `1.59.4`; consulte [validação pela CLI do LikeC4](likec4-validation.md).

## Códigos de saída

| Código | Significado |
| ---: | --- |
| `0` | Comando concluído com sucesso. |
| `2` | Comando ou argumentos incorretos. |
| `3` | Snapshot/modelo v1 inválido. |
| `4` | Validação LikeC4 falhou ou a CLI configurada não conseguiu validar. |
| `5` | Operação local de arquivo/caminho falhou ou a política de overwrite bloqueou a operação. |
| `6` | Provedor de inferência local indisponível ou tempo limite excedido. |

O código original do LikeC4 aparece apenas como contexto diagnóstico e é mapeado para o código `4` do Repo2C4.

## Limite de inferência

O fluxo suportado é deliberadamente separado:

```text
repositório local
    -> inspect
    -> snapshot de evidências
    -> proposta/revisão humana
    -> ArchitectureModel
    -> generate
    -> arquivos LikeC4
    -> validate
```

O Repo2C4 não converte automaticamente todo projeto .NET em container C4. Referências de build e candidatos de pacote/runtime permanecem evidências ou hipóteses até serem explicitamente revisados no modelo.

## Exemplo reproduzível

Consulte o [exemplo end-to-end versionado](../examples/end-to-end/README.md). Ele usa a fixture apenas-biblioteca para demonstrar que a ausência de executável é preservada: o modelo C2 revisado não fabrica um container.
