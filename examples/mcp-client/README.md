# MCP client example

This directory contains reusable, vendor-neutral natural-language instructions for an MCP client:

- [English prompt](architecture-review-prompt.md)
- [Prompt em português](architecture-review-prompt.pt-BR.md)

Use them only after configuring the Repo2C4 local stdio server as documented in [docs/mcp-client.md](../../docs/mcp-client.md) or [docs/mcp-client.pt-BR.md](../../docs/mcp-client.pt-BR.md).

The prompts intentionally stop before filesystem writes and require explicit user approval of a repository-relative destination. They do not authorize Git changes, PR creation or automatic promotion of hypotheses into confirmed architecture.
