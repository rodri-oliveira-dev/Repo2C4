# Relatório de evidências e revisão arquitetural

[English](evidence-report.md)

O Repo2C4 mantém relatórios determinísticos e somente de metadados. Eles podem conter IDs arquiteturais, IDs de evidência, caminhos relativos ao repositório e linhas, mas não copiam corpos de source, valores de configuração, tokens, segredos ou caminhos absolutos.

## Relatório C1/C2

`evidence-report.md` é gerado a partir do `ArchitectureModel` revisado e ajuda a revisar:

- afirmações e IDs de evidência;
- hipóteses `requiresReview`;
- warnings/diagnósticos de scan;
- evidências ausentes ou origens não resolvidas.

Fluxo recomendado:

1. inspecione e obtenha o snapshot;
2. construa/revise o modelo;
3. gere preview;
4. revise `requiresReview` e siga a proveniência até source/caminho relativo;
5. confirme somente quando houver suporte adequado;
6. regenere e valide LikeC4.

Sinal estático não vira runtime edge confirmado só porque aparece no relatório.

## Relatório Semantic C3

Quando C3 é solicitado a partir de snapshot com `semanticC3Facts`, a geração adiciona `semantic-c3-evidence-report.md`.

Ele lista:

- IDs e categorias de componentes/relações;
- localizações relativas;
- classes de sinais estruturais;
- sinais de confiança ausentes;
- review status;
- diagnósticos limitados do grafo.

Uma relação interna C3 `confirmed` significa que a política estática configurada foi satisfeita (por exemplo wiring suportado + colaboração resolvida). Não significa tracing runtime. Fronteiras de componentes continuam revisáveis.

No MCP, `get_evidence_report` continua sendo o resumo C1/C2 vinculado ao snapshot da sessão; o relatório semântico faz parte dos arquivos retornados por `generate_likec4` quando Semantic C3 está ativo.

Veja [Semantic C3](semantic-c3.pt-BR.md).
