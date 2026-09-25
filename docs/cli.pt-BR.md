# CLI offline do Repo2C4

A CLI da Fase 2 disponibiliza três comandos locais. Nenhum deles chama provedor de IA, abre pull request, avalia MSBuild ou transforma candidatos de pacote/projeto em arquitetura runtime confirmada.

## Comandos

### Inspect

```bash
repo2c4 inspect --repository PATH --output snapshot.json
```

`inspect` inventaria um repositório local explicitamente selecionado e extrai apenas as evidências limitadas suportadas pelo Core. A saída é JSON v1 canônico.

O identificador do repositório é derivado de forma determinística do nome do diretório raiz selecionado. Ele é um rótulo local estável, não uma identidade globalmente única.

O arquivo de snapshot não pode existir previamente. Use outro caminho caso exista.

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

Arquivos existentes não são substituídos por padrão. Para substituir explicitamente as quatro saídas fixas:

```bash
repo2c4 generate --model architecture.json --output DIR --overwrite
```

`evidence-report.md` relaciona as afirmações do modelo aos IDs de evidência e às localizações relativas do repositório, separa hipóteses pendentes, avisos de varredura e itens sem suporte, sem copiar corpos de código nem valores sensíveis. Consulte [relatório de evidências e revisão arquitetural](evidence-report.md).

Para solicitar C3 de exatamente um container C2 revisado, adicione `--c3-container ID`:

```bash
repo2c4 generate --model architecture.c2.json --output DIR --c3-container el_web
```

Sem essa opção nenhum arquivo C3 é gerado. Uma seleção válida adiciona `components.c4` e `c3.views.c4`; os demais containers C2 não recebem vistas de componentes automaticamente. A proposta C3 é limitada, mantém sinais estáticos/candidatos sob revisão e falha quando o container selecionado não possui evidência suficiente.

O diretório de saída e qualquer arquivo de destino já existente não podem ser symlink, junction ou reparse point. Os nomes gerados são fixos pelo Repo2C4 e não podem ser definidos pelo conteúdo do modelo.

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
