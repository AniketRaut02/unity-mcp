# Unity MCP Bridge

Connect Claude Code, Codex, or any [Model Context Protocol](https://modelcontextprotocol.io)
client to the Unity Editor. Create GameObjects, write scripts, configure physics,
build UI, manage assets, and construct custom Behavior Trees — all from your AI
coding assistant, with real safety guardrails (destructive actions require
explicit confirmation, every call is audit-logged, filesystem access is
sandboxed to `Assets/`).

## Install

**Package Manager (recommended):**
`Window → Package Manager → + → Add package from git URL...` and paste this
repository's URL, or `Add package from disk...` pointing at this folder's
`package.json` if you have it locally.

**Python server:**
```bash
cd python
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
```

## Quick start

1. Open your Unity project. The MCP bridge starts automatically — check the
   Console for `[MCP] Listening on 127.0.0.1:<port>`.
2. Open `Window → Unity MCP → Setup`. Set the Python server location (the
   folder containing `workflows.py` and `server.py`), then click **Configure**
   next to Claude Code, Codex, Cursor, or Antigravity (whichever you use) to
   register the server automatically — no terminal commands needed. The
   window shows which client was configured most recently and color-codes
   each section (green = good/configured, yellow = needs attention, red =
   blocked) so the state is visible at a glance.
3. Restart your MCP client session. Ask it to do something in Unity — "create
   a red cube at the origin" is a good first test.

## What's included

| Area | Tools | Notes |
|---|---|---|
| Scene / Component / Query | 15 | Always visible — the `core` group |
| Scripting | 6 | Create/read/update/delete `.cs` files, compile-status polling |
| Physics | 6 | Colliders, Rigidbody, forces, raycasting |
| Assets | 7 | Prefabs, materials, ScriptableObjects |
| UI | 6 | Canvas, buttons, layout, RectTransform |
| Composite tools | 5+ | `batch_execute`, `manage_tools`, a full custom Behavior Tree framework, and anything you build yourself |

Only the `core` group (15 tools + `batch_execute` + `manage_tools`) is visible
by default — everything else is toggled on with the `manage_tools` tool
(`activate`/`deactivate`/`list_groups`) to keep your assistant's context
focused. Ask it "what tool groups are available?" to see the full list.

## Adding your own tools

- **New Unity-side behavior** — write a `[MCPTool]`-decorated static method in
  C#; nothing else to register. See `docs/writing-custom-tools.md` for the
  full guide.
- **Chaining existing tools** — write a Python `@workflow`-decorated composite
  tool directly in `python/unity_mcp_server/custom_workflows.py`, following
  the pattern of the built-ins in `workflows.py` (`batch_execute`,
  `create_behavior_tree`, ...). No Unity restart needed, just reconnect your
  MCP client session.

## Transports

Defaults to stdio (what Claude Code / Codex expect). Streamable HTTP is also
available (`UNITY_MCP_TRANSPORT=http`) for driving Unity from a different
machine — see `python/unity_mcp_server/http_transport.py` for setup and its
security requirements (binding beyond `127.0.0.1` requires an auth token).

## Security notes

- The bridge only ever binds to `127.0.0.1` — never your network.
- Every session gets a fresh random token, published alongside its actual
  port in `Library/MCP/session.json`, so multiple Unity projects on one
  machine can never cross-connect.
- Destructive tools (`delete_gameobject`, `delete_script`, `delete_asset`, ...)
  require an explicit `confirm: true` argument, enforced centrally — no
  individual tool can silently skip this.
- Every tool call is logged to `Library/MCP/audit.log`.

## More documentation

- `docs/writing-custom-tools.md` — full custom-tool authoring reference.
- `CHANGELOG.md` — version history.
- If something looks wrong, `python diagnose_bridge.py /path/to/YourUnityProject`
  is the fastest way to check the bridge's actual state before digging further.
