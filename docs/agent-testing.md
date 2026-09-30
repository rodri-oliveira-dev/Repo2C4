# Agent deterministic testing

The mandatory Agent test path is deterministic and does not require a hosted LLM, Ollama, Internet access for inference, or provider credentials.

## Harness

`tests/Repo2C4.Agent.Tests/ScriptedAgentHarness.cs` drives the Microsoft Agent Framework with a scripted `IChatClient` while starting the real `Repo2C4.Mcp` process over stdio against temporary copies of checked-in repository fixtures.

The harness can reproduce:

- MCP tool-call decisions and function results;
- bounded correction attempts;
- Agent Framework write-approval requests;
- approve, deny and conflict outcomes;
- cancellation and MCP process cleanup;
- repository prompt-injection data without exposing raw repository content to the chat client.

For retry-focused tests, only `validate_likec4` is replaced with deterministic validation outcomes. Inspection, evidence retrieval, LikeC4 generation previews, evidence reports and the MCP process remain real.

When `REPO2C4_LIKEC4_INTEGRATION=1`, an additional E2E path uses the real `validate_likec4` tool after CI installs the pinned LikeC4 CLI.

## Security invariants

Mandatory tests run without `OPENAI_API_KEY`. They verify that no managed LikeC4 file is written before a real Agent Framework approval request receives an explicit local approval, that rejected requests write nothing, conflicts preserve human content, unsupported architecture remains `requiresReview`, and prompt-like repository content never becomes agent instructions.

## Optional provider smoke

`.github/workflows/agent-provider-smoke.yml` is `workflow_dispatch` only and is not part of pull-request or push gates. It requires an explicitly supplied OpenAI model ID plus the repository secret `OPENAI_API_KEY`, then runs one conservative analysis against the local `library-only` fixture.

The provider smoke never writes architecture files and never runs automatically for forks or pull requests.
