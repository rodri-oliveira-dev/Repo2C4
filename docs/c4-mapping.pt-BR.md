# Política de mapeamento C4

[English](c4-mapping.md)

Este guia resume as regras de mapeamento evidence-first do Repo2C4 para C1, C2 e Semantic C3. A regra principal é separar **observação de repositório** de **decisão arquitetural**.

## C1/C2

- projeto .NET não implica automaticamente container;
- `ProjectReference`, package, SDK ou source candidate não confirma comunicação runtime;
- container/deployment boundary permanece `requiresReview` quando sustentado apenas por sinais estáticos;
- relações precisam apontar para elementos existentes e manter proveniência;
- modelos parciais são preferíveis a completar a arquitetura por suposição.

A ausência de evidência não autoriza criar actor, sistema externo, container ou relação. Hipóteses úteis devem permanecer explícitas em `reviewReason`.

## Semantic C3

Semantic C3 é aditivo ao C1/C2 revisado. **Classe não é componente**.

As responsabilidades suportadas incluem fronteira HTTP, application/use case, worker, publisher/consumer, persistence e integration adapter. Uma classe só é promovida quando sinais estruturais sustentam aquela responsabilidade. Nome, package ou referência de projeto isolados não bastam.

Para relações internas:

- wiring DI/handler/parâmetro é um sinal;
- invocação de símbolo resolvida estaticamente é outro;
- pares suportados só podem ser confirmados quando os sinais exigidos convergem;
- relações fracas, ciclos ambíguos, self-loops e edges internas cruzando containers são omitidos em vez de inventados.

Para relações externas, C3 reutiliza peer e evidência de relação já presentes no modelo C1/C2. PostgreSQL, Redis, broker e APIs externas não são criados apenas pelo nome de um tipo.

## Pipeline integrado

A inspeção local persiste `semanticC3Facts` limitados. Um report DotNetRepoInspector compatível pode adicionar `externalIntegrationEvidence` sanitizado. Depois, um modelo C2 revisado seleciona um ou mais containers e a geração reutiliza esses fatos offline.

Snapshots antigos sem fatos semânticos continuam no agrupamento C3 genérico compatível.

Veja [Semantic C3](semantic-c3.pt-BR.md) para categorias, budgets, dogfooding e limitações, e [relatório de evidências](evidence-report.pt-BR.md) para provenance.
