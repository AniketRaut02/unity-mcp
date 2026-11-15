# Unity MCP Bridge

Connect Claude Code, Codex, Cursor, VS Code (Copilot), Claude Desktop, Antigravity, or any
[Model Context Protocol](https://modelcontextprotocol.io) client to the Unity Editor: 300+
tools to create GameObjects, write scripts, configure physics, build UI, manage assets and more,
with real safety guardrails.

## Install

1. Add this package (`Window → Package Manager → + → Add package from git URL...`):
   `https://github.com/AniketRaut02/unity-mcp.git?path=/unity/com.unitymcp.bridge`
2. Install [uv](https://docs.astral.sh/uv/) if you don't have it (the Setup window shows the
   one-line command). The MCP server ships inside this package (`Server~/`); uv runs it.
3. `Window → Unity MCP → Setup` → **Configure all detected clients**, then restart your client.

The client configs are kept current automatically when this package updates.

## Windows

- **Setup** — bridge status, uv status, client configuration.
- **Tool Groups** — disable groups, Read-Only Mode, the danger tier, package and menu allowlists.
- **Approvals** — approve or deny danger-tier calls (opens by itself when one is requested).
- **Tool Builder** — compose new tools from existing ones without code.

## Your own tools

- **New Unity-side behavior** — a `[MCPTool]`-decorated static C# method; see
  `docs/writing-custom-tools.md` in the repository.
- **Composite tools** — the Tool Builder, or `@workflow` functions in
  `<your project>/UnityMCP/custom_workflows.py` (loaded when the server starts).

## Security notes

- Everything binds `127.0.0.1`. The Editor authenticates to the server with a per-launch token
  published in `Library/MCP/unity.json`.
- Builds, build/project settings, package changes and non-allowlisted menu items are off until
  enabled in Tool Groups, and then need approval in the Editor for each call.
- Destructive tools require `confirm: true`; every call is logged to `Library/MCP/audit.log`.

## More

- `CHANGELOG.md` — version history.
- Troubleshooting: run `uv run --frozen python diagnose_bridge.py /path/to/YourProject` from
  this package's `Server~` folder.
