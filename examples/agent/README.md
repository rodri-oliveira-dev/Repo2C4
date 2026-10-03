# Repo2C4 Agent end-to-end example

This example uses the checked-in `library-only` fixture to demonstrate the Agent safety flow without hiding the review boundary.

## 1. Install the tools

```bash
dotnet tool install --global Repo2C4.Agent
dotnet tool install --global Repo2C4.Mcp
dotnet tool install --global Repo2C4.Cli
npm install --global likec4@1.59.4
```

Run from the Repo2C4 checkout so the fixture path is available. Confirm that Ollama is running and that the selected model is installed:

```bash
ollama list
# If needed:
ollama pull YOUR_LOCAL_MODEL
```

## 2. Analysis only

With Ollama running locally:

```bash
repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --goal "Inspect only through Repo2C4 MCP. Produce conservative C1/C2 documentation and keep unsupported boundaries requiresReview."
```

The Agent launches `repo2c4-mcp` over stdio, asks MCP for repository evidence, proposes C1/C2 through preview-only generation, obtains evidence reports, validates LikeC4, and performs at most the configured bounded correction attempts.

For this fixture, a model must not promote a runtime/deployment boundary to `confirmed`: the repository contains only a library project.

No managed `.c4` file is created in this step. A successful run prints `Status: completed` or `Status: requires_review`, followed by the run ID and bounded execution counters.

### Optional selective multi-container C3

If a reviewed C2 proposal contains multiple evidence-supported containers, repeat `--c3-container` to authorize only the candidates the Agent may consider:

```bash
repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --c3-container "container_api" \
  --c3-container "container_worker" \
  --goal "Produce C1/C2 and propose C3 only for relevant, evidence-supported authorized containers."
```

The repeated values are an allow-list, not a request to expand every container. The Agent selects only the supported subset, with a maximum of 8 authorized IDs. Do not derive IDs from project names; they must identify reviewed C2 containers.

## 3. Optional protected write

Choose a repository-relative destination explicitly:

```bash
repo2c4-agent \
  --provider ollama \
  --model YOUR_LOCAL_MODEL \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --goal "Prepare conservative C1/C2 documentation for local review." \
  --write-destination "docs/generated"
```

With `--write-destination "docs/generated"`, the approved C1 and C2 workspaces are written under `docs/generated/c1` and `docs/generated/c2`.

The expected control sequence is:

```text
goal
  -> MCP evidence
  -> proposal
  -> evidence report
  -> dry-run LikeC4 preview
  -> validation
  -> Agent Framework approval request
  -> local human decision
  -> MCP protected write
```

Before the approval prompt, no managed architecture file is written. Answering no/cancel performs no write. Answering yes permits only the immutable validated plan that was shown; a stale preview or conflicting human edit aborts instead of overwriting.

## 4. Inspect the result

When approved successfully:

```bash
repo2c4 validate --output examples/fixtures/library-only/docs/generated/c1
repo2c4 validate --output examples/fixtures/library-only/docs/generated/c2
```

Review the generated `evidence-report.md` files and any `requiresReview` items before treating the documentation as accepted architecture.

## OpenAI variant

Use a hosted provider only with explicit consent and an environment-provided secret:

```bash
export OPENAI_API_KEY="from-your-secret-store"

repo2c4-agent \
  --provider openai \
  --model YOUR_OPENAI_MODEL \
  --allow-external-ai \
  --repository-root "$(pwd)/examples/fixtures/library-only" \
  --goal "Produce conservative C1/C2 documentation from Repo2C4 MCP evidence."
```

Do not put the API key in CLI arguments, repository files or shell history. The MCP child process does not inherit it.

See [Agent documentation](../../docs/agent.md) for limits, cancellation, observability and troubleshooting.
