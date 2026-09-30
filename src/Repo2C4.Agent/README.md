# Repo2C4 Agent

Repo2C4 Agent is a local .NET architecture-documentation agent built with Microsoft Agent Framework. It acts as an independent client of `Repo2C4.Mcp`, using evidence-first analysis, bounded validation/correction and explicit human approval before protected writes.

## Install

```bash
dotnet tool install --global Repo2C4.Agent --version 1.0.0
dotnet tool install --global Repo2C4.Mcp --version 1.0.0
```

The Agent requires .NET 10 and the Repo2C4 MCP server. LikeC4 is installed separately for validation.

## Ollama example

```bash
repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "/absolute/path/to/repository" \
  --goal "Document the current C1 and C2 architecture conservatively."
```

Without `--write-destination`, the Agent performs analysis/validation only and does not persist managed `.c4` files.

## OpenAI example

Hosted AI requires explicit consent and an environment-provided credential:

```bash
export OPENAI_API_KEY="from-your-secret-store"

repo2c4-agent \
  --provider openai \
  --model YOUR_OPENAI_MODEL \
  --allow-external-ai \
  --repository-root "/absolute/path/to/repository" \
  --goal "Document the current C1 and C2 architecture conservatively."
```

## Protected write

Add a repository-relative destination only when you want a validated proposal to reach the local human-approval step:

```bash
repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "/absolute/path/to/repository" \
  --goal "Prepare C1/C2 documentation for review." \
  --write-destination "docs/architecture"
```

The Agent uses the MCP write protections and Microsoft Agent Framework HITL approval before applying the immutable validated plan.

## Documentation

- Agent guide: https://github.com/rodri-oliveira-dev/Repo2C4/blob/main/docs/agent.md
- End-to-end example: https://github.com/rodri-oliveira-dev/Repo2C4/tree/main/examples/agent
- Distribution/security: https://github.com/rodri-oliveira-dev/Repo2C4/blob/main/docs/distribution.md

Source and issues: https://github.com/rodri-oliveira-dev/Repo2C4
