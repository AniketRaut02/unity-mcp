# Changelog

All notable changes to this package are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows
[Semantic Versioning](https://semver.org/).

## [1.0.0] — Phase 7: streamable HTTP + UPM packaging

First release packaged as a proper Unity Package Manager package (this
`package.json` — previously distributed as a raw `Assets/Editor/MCP/` folder
to copy in by hand). Marking this 1.0.0 rather than another 0.x bump because
by this point the full stack has been validated end-to-end in a real Unity
Editor, not just against the test suite.

### Added
- Streamable-HTTP transport (`UNITY_MCP_TRANSPORT=http`) as an alternative to
  stdio, for driving Unity from a different machine or sharing one running
  server across multiple client connections. Binds to loopback only by
  default; binding beyond loopback is refused outright unless
  `UNITY_MCP_HTTP_TOKEN` is set, enforced with a real bearer-token check on
  every request, not just documented as a recommendation.
- `CHANGELOG.md`, `LICENSE.md` (placeholder — no license chosen yet), and
  this package's own end-user `README.md`.

### Fixed
- `UnityBridgeClient.connect()` didn't wrap raw socket failures
  (`ConnectionRefusedError` and other `OSError`s) into `BridgeError`, so a
  genuinely unreachable Unity bridge could crash an entire MCP call instead
  of degrading gracefully to the reduced (Python-workflow-only) tool list
  the rest of the server is designed to fall back to.
- The MCP client's tool list could get permanently stuck in that degraded
  state even after Unity fully recovered on a new port (a domain reload
  always causes a port change, since `MCPServer.Start()` searches for a free
  port rather than assuming the previous one is still available) — nothing
  told the client to look again. Fixed with
  `UnityBridgeClient.add_reconnect_listener()`, which fires a
  `tools/list_changed` notification on every reconnect, not just when
  `manage_tools` changes which groups are active (which was the only case
  handled before this fix).

### Changed
- Repository layout: C# source moved from `Assets/Editor/MCP/` to
  `com.unitymcp.bridge/Editor/` to match standard UPM package structure.
  Install via git URL or "Add package from disk" in Package Manager instead
  of copy-pasting a folder.

## [0.6.0] — Phase 6: visual Tool Builder

### Added
- `Window → Unity MCP → Tool Builder` — assemble a new composite tool from
  existing atomic tools via a form (no C#, no hand-written Python), which
  generates a real `@workflow`-decorated Python function into
  `custom_workflows.py`. Validates every step against the live tool registry
  as it's built.
- `MCPToolBuilderSettings.IsInsideProject()` guard: refuses to generate if
  the configured Python server location is nested inside the Unity project,
  since writing there can trigger an unrelated Unity reimport/recompile and
  disconnect the bridge.

## [0.5.0] — Setup window, Tool Groups, custom-tool authoring polish

### Added
- `Window → Unity MCP → Setup` — live bridge status (including a connected-
  client count) and one-click Claude Code / Codex configuration, replacing
  manual terminal commands.
- Tool Groups: 6 groups (`core` always active; `scripting`/`physics`/
  `assets`/`ui`/`behavior_tree` toggleable per session), plus a
  `manage_tools` workflow tool (`list_groups`/`activate`/`deactivate`/`reset`).
  A visibility mechanism for prompt economy, not a security boundary — every
  real safety check still applies regardless of group state.
- `[MCPParam]` for per-parameter schema descriptions, applied across every
  tool. Enum-typed parameters now emit a real JSON Schema `"enum"` constraint
  instead of a bare `"type": "string"`.
- `docs/writing-custom-tools.md` — the complete custom-tool authoring guide.

### Fixed
- `manage_tools activate`/`deactivate` changed server-side state correctly
  but never told an already-connected client to refetch its tool list (MCP
  clients cache `list_tools()` and don't auto-refresh) — fixed by declaring
  the `tools_changed` capability and sending the notification.

## [0.4.0] — Phase 5: composite/workflow tools

### Added
- The Python-side workflow-tool registry (`workflows.py`) — composite tools
  built by orchestrating atomic Unity tools, exposed to an MCP client
  identically to a real Unity tool.
- A working custom Behavior Tree framework (`Sequence`/`Selector`/
  `ActionNode`/`BTRunner`), generated into a project via the existing
  `create_script`/`update_script` tools — no C# changes were needed to build
  the whole composite-tool layer.
- `create_behavior_tree`, `add_behavior_tree_node`,
  `scaffold_behavior_tree_framework` workflow tools.

## [0.3.0] — Phase 4: performance

### Added
- `batch_execute` — real wire-level batching (one round trip for N tool
  calls), not a Python-side loop over individual calls.
- Fast/slow priority queue on the Unity side, so quick queries never queue
  behind a domain-reload-triggering tool.
- `get_hierarchy` result caching, invalidated on `EditorApplication.hierarchyChanged`.

## [0.2.0] — Phase 3: tool breadth

### Added
- Scripting module (6 tools): create/read/update/delete scripts, list
  scripts, compile-status polling via `CompilationPipeline` events.
- Physics module (6 tools): colliders, Rigidbody configuration/state,
  forces, raycasting.
- Assets module (7 tools): prefabs, materials, ScriptableObjects, generic
  asset listing/deletion.
- UI module (6 tools): Canvas, UGUI elements (a composite tool internally —
  Button/InputField chain several atomic operations), RectTransform,
  layout groups, color.

## [0.1.0] — Phase 1–2: core bridge, registry, and security

### Added
- TCP bridge between a Python MCP server and the Unity Editor, with a
  background-thread listener and main-thread-only dispatch via
  `EditorApplication.update`.
- stdio MCP server, 15 initial Scene/Component/Query tools, handshake auth.
- Centralized destructive/confirm gate (`[MCPTool(destructive: true)]`),
  audit log, per-connection rate limiting, filesystem path guard.

### Fixed
- **Port/token collision incident**: a hardcoded global port meant multiple
  Unity processes on one machine could cause a client to connect to the
  wrong project's listener, whose token would never match. Fixed by having
  `MCPServer.Start()` search for a free port and publish `(port, token)`
  together via `Library/MCP/session.json`, re-read fresh on every reconnect.
