---
name: unity-mcp
description: How to drive the Unity Editor through the Unity MCP Bridge tools (manage_tools, batch_execute, scene/asset/script tools). Use whenever a task involves the open Unity project - creating or changing GameObjects, components, scenes, prefabs, materials, C# scripts, lighting, UI, builds, or reading the hierarchy and console.
---
<!-- Installed by Unity MCP Bridge (Window > Unity MCP > Setup). Regenerated when the package updates; edit the package's Skills~/unity-mcp/SKILL.md instead of this copy. -->

# Driving Unity through Unity MCP Bridge

## Find the right tool first
Only the `core` group is visible at the start; 300+ more tools are hidden in groups.
1. `manage_tools(action="search", query="a few keywords")` finds tools and groups without loading their schemas.
2. Using one or two hidden tools: `manage_tools(action="describe", tools=[...])`, then
   `manage_tools(action="call", tool="...", arguments={...})`. The group stays hidden.
3. Using a group heavily: `manage_tools(action="activate", groups=[...])`, call the tools
   directly, and `deactivate` it when the task moves on.

## Work efficiently
- Several quick changes in a row: one `batch_execute` call (up to 25 calls) instead of many.
  Slow tools (script create/update/delete and anything that recompiles) can't be batched.
- Reads are summary-first. `get_hierarchy` returns the top level with each node's `childCount`;
  expand with `path`, `depth` or `limit`. Asset/script/GameObject lists over 100 items return a
  total, per-folder counts and the first page: narrow the search or page with `offset`.
- Hierarchy paths look like `Parent/Child`. Asset and script paths are relative to `Assets/`.

## Scripts and recompiles
- After `create_script`, `update_script` or `delete_script`, poll `get_compile_status` until
  `isCompiling` is false and check its errors before using the new code (for example before
  `add_component` with a new type).
- A recompile reloads Unity for a few seconds. Calls made meanwhile wait and then complete. If a
  call says Unity "is reloading ... retry shortly", wait a moment and retry it once.
- After play-mode actions or script changes, check `get_console_logs` for errors.

## Safety rules
- Destructive tools need `confirm: true`. Only pass it when the user asked for that deletion
  or overwrite.
- Some tools are in a danger tier (Player builds, build/project settings, package installs,
  menu items outside the allowlist). If one returns `status: "awaiting_approval"`, it has NOT
  run: ask the user to approve it in Unity (Window > Unity MCP > Approvals), then call it again
  with exactly the same arguments. If it reports the user denied it, don't retry.
- A tool reported as unknown may be in a group the user disabled. Don't look for workarounds.
- Undo exists (`undo`, `redo`), but prefer asking before large or irreversible changes.
