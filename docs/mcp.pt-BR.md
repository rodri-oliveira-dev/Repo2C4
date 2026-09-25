# Servidor MCP stdio e política de acesso local

A issue #13 estabelece somente o transporte MCP local e o limite de acesso ao filesystem. Ela não expõe ferramentas de inspeção do repositório, recuperação de evidências ou geração LikeC4. Essas capacidades permanecem reservadas às próximas issues da Fase 3.

## Iniciar o servidor

Compile a solução e informe exatamente uma raiz local autorizada:

```bash
dotnet src/Repo2C4.Mcp/bin/Release/net10.0/Repo2C4.Mcp.dll \
  --repository-root /caminho/absoluto/do/repositorio
```

Como alternativa de configuração local, defina `REPO2C4_REPOSITORY_ROOT` e omita a opção de linha de comando. Quando os dois forem fornecidos, a linha de comando tem precedência.

O servidor não exige chave de provedor de IA, conta externa, endpoint de rede ou configuração de modelo.

## Limite de protocolo e logs

O host usa o SDK .NET mantido `ModelContextProtocol.Core` com transporte stdio. `stdout` fica reservado exclusivamente às mensagens do protocolo MCP. Ajuda, falhas de configuração e diagnósticos controlados são escritos em `stderr`; detalhes de exceção e stack traces não são emitidos no canal do protocolo.

O processo trata cancelamento pelo token do host e por Ctrl+C. O fechamento de stdin encerra a sessão stdio de forma controlada.

A issue #13 registra propositalmente uma coleção vazia de ferramentas MCP. O cliente consegue inicializar e executar `tools/list`, mas recebe uma lista vazia. `inspect_repository`, recuperação de evidências e operações LikeC4 não são introduzidas nesta etapa.

## Raiz autorizada do repositório

A raiz configurada deve:

- ser um caminho absoluto e totalmente qualificado;
- existir previamente como diretório;
- não conter travessia de diretório pai com `..`;
- não ser link simbólico, junction ou outro reparse point.

Caminhos recebidos por futuras ferramentas MCP devem ser relativos a essa raiz. A resolução rejeita caminhos absolutos, travessia de diretório pai, normalização para fora da raiz, caminhos inexistentes e qualquer componente simbólico/reparse. Links são rejeitados mesmo quando apontam para dentro da própria raiz, mantendo a política MCP alinhada à política conservadora já adotada pelo scanner do Core.

Essas verificações gerenciadas reduzem escapes acidentais do checkout aprovado, mas não prometem garantias atômicas contra alterações maliciosas concorrentes no filesystem. Execute o Repo2C4 sobre um checkout confiável, estável, preferencialmente somente leitura e com privilégio mínimo.

## Limites definidos pela issue #13

A política do host da Fase 3 define:

- timeout de inicialização: 30 segundos;
- teto de execução de ferramenta: 30 segundos;
- teto de inspeção: 1.000 arquivos, alinhado ao padrão atual do scanner do Core;
- teto de resposta MCP: 1.048.576 bytes.

Somente o timeout de inicialização é exercitado pelo transporte na issue #13, pois ainda não existem ferramentas de domínio. As próximas issues que adicionarem ferramentas devem consumir esses limites, sem criar políticas paralelas.

## Limite arquitetural

O projeto MCP referencia o Core; o Core não referencia o SDK MCP. Os contratos v1 existentes continuam sendo a fonte de verdade.

O servidor não faz inferência arquitetural e não incorpora provedor de IA nem seleção de modelo. Evidência estática, hipótese e fato arquitetural confirmado continuam distintos. `ProjectReference`, pacotes e sinais `.candidate` não se tornam relações runtime confirmadas apenas por serem futuramente expostos via MCP. A interpretação arquitetural continua sendo responsabilidade do cliente MCP.
