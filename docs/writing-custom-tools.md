# Writing Your Own Unity MCP Tools

This is the reference for adding a new tool to this platform. If you've read
any of it before and just want the pattern, skip to §1; the rest covers every
knob available and the guardrails to know about.

## §1. The five-minute version

A tool is a `public static` C# method, decorated with `[MCPTool]`, inside a
static class under `com.unitymcp.bridge/Editor/Tools/` (anywhere under this
package's `Editor/` folder
actually works, but that's where the rest of them live). Nothing else —
Unity's reflection scan at Editor load finds it automatically. No registration
step, no restart beyond the recompile Unity does anyway.

```csharp
using UnityEditor;
using UnityEngine;
using UnityMCP;

namespace UnityMCP.Tools
{
    public static class MyCustomTools
    {
        [MCPTool("spawn_coin", "Instantiates a coin pickup prefab at a world position.")]
        public static MCPResult SpawnCoin(
            MCPToolContext ctx,
            [MCPParam("World-space X position.")] float x,
            [MCPParam("World-space Y position.")] float y,
            [MCPParam("World-space Z position.")] float z)
        {
            var go = new GameObject("Coin");
            go.transform.position = new Vector3(x, y, z);
            Undo.RegisterCreatedObjectUndo(go, "MCP: Spawn Coin");

            return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
        }
    }
}
```

That's the whole tool. Claude Code / Codex will see `spawn_coin` in their tool
list the next time they call `list_tools` (immediately, if you're using
`batch_execute` or `manage_tools`'s `list_groups` to refresh; otherwise on
their next turn), with a schema built entirely from your method signature.

## §2. `[MCPTool]` — the five things you can configure

```csharp
[MCPTool(name, description, latencyTier: MCPLatencyTier.Fast, destructive: false, group: "core")]
```

| Argument | Default | What it controls |
|---|---|---|
| `name` | *(required)* | The tool name a client calls. Must be globally unique — the registry rejects a duplicate at scan time with a clear Console error rather than silently shadowing one tool with another. |
| `description` | *(required)* | What the agent reads to decide when/how to call this. Worth being as specific as your other parameter descriptions — see §4. |
| `latencyTier` | `Fast` | Set to `MCPLatencyTier.Slow` if your tool triggers a domain reload (calls `AssetDatabase.Refresh()`, writes a `.cs` file, etc. — see §5). This affects queueing priority and makes the tool ineligible for `batch_execute`. |
| `destructive` | `false` | Set `true` if the tool deletes or irreversibly overwrites something. The registry automatically adds a required `confirm: true` argument to the schema and refuses to invoke your method unless it's set — **your method never sees or needs a `confirm` parameter**; don't add one. See §6. |
| `group` | `"core"` | Which tool group this belongs to (see the Tool Groups doc). `"core"` tools are always visible to a client; anything else stays hidden until activated via `manage_tools`. If you're adding a handful of tools for one feature area, give them their own group name — clients can then activate exactly what they need. |

## §3. Parameter types the registry understands

The registry binds JSON arguments to your method's C# parameters by name.
Supported parameter types:

- `string`, `int`, `long`, `float`, `double`, `bool` — plain, and nullable
  (`float?`, `bool?`, ...) for "optional, omit to mean something specific"
  semantics (see §7).
- Any `enum` you define — gets a proper JSON Schema `"enum": [...]` listing
  every value by name, so an agent sees the exact valid set instead of
  guessing from prose. (`add_collider`'s `MCPColliderType` is a good example
  to copy from.)
- `MCPToolContext` — optional first parameter, not part of the schema, gives
  you `ctx.RequestId` if you need it. Most tools don't.

**Not currently supported directly:** `Vector3`, `Vector2`, `Color`, or any
other struct with more than one logical value. Every existing tool that needs
one (colliders' `size`/`center`, Rigidbody `velocity`, RectTransform anchors)
takes it as separate flat float parameters (`sizeX`, `sizeY`, `sizeZ`, ...)
instead — see `PhysicsTools.AddCollider` or `UITools.SetRectTransform` for the
pattern, including the "read into a local, override only the axes actually
supplied, write back" idiom every one of them uses for optional per-axis
edits. Follow it for consistency rather than inventing a new convention.

## §4. `[MCPParam]` — per-parameter descriptions

```csharp
[MCPParam("description text")] float x
```

Purely additive — a parameter with no `[MCPParam]` still works exactly as
before, just without a `"description"` in its schema entry. Use it whenever a
parameter's purpose isn't obvious from its name alone:

- **Do:** `[MCPParam("Local X position. Omit to leave unchanged.")]`
- **Skip it for:** a `path` parameter that's obviously "hierarchy path of the
  target GameObject" in every tool that has one — not worth annotating 40
  times if the name alone is unambiguous. (Though for consistency, every
  existing `path` parameter *is* annotated — look at any existing tool file
  for the exact phrasing to match.)

Good parameter descriptions measurably affect tool-call accuracy — this is
the one part of authoring a tool that's worth the extra two minutes per
parameter, more than most other polish.

## §5. Fast vs. Slow — and why it matters beyond labeling

`MCPLatencyTier.Slow` isn't just documentation. It changes real behavior:

1. **Queueing priority.** `MCPCommandDispatcher` drains the fast queue before
   touching the slow one each Editor tick (with one slot per tick reserved
   for slow calls so they're never fully starved). A burst of Fast-tier
   queries never has to wait behind a Slow call that's already queued.
2. **Batch eligibility.** `batch_execute` rejects any Slow-tier tool per-item
   with a clear message rather than either failing the whole batch or,
   worse, letting a domain reload wipe out every call queued after it in the
   same batch. If your tool can trigger a domain reload, mark it `Slow` —
   don't rely on callers figuring this out from a docstring.

If you're not sure whether your tool counts: does it call
`AssetDatabase.Refresh()`, write a `.cs` file, or do anything else that could
make Unity recompile? If yes, `Slow`. Everything else defaults to `Fast`.

## §6. Destructive tools

```csharp
[MCPTool("delete_something", "...", destructive: true)]
public static MCPResult DeleteSomething(MCPToolContext ctx, string path)
{
    // no confirm parameter here — the registry already handled it
    ...
}
```

The registry adds a required `confirm` boolean to the schema and checks it
*before* your method is ever invoked — a call without `confirm: true` never
reaches your code at all. This is enforced centrally specifically so it can't
be forgotten or implemented inconsistently tool-by-tool. If you find yourself
writing an `if (!confirm) return MCPResult.Fail(...)` check inside your
method, delete it and add `destructive: true` to the attribute instead — you
almost certainly don't have (and don't want) a `confirm` parameter in your
method signature.

## §7. The optional-parameter override pattern

For "set some properties, leave the rest as they are" tools (most `set_*`
tools), use nullable parameters and only write the value if it was actually
supplied:

```csharp
public static MCPResult SetSomething(
    MCPToolContext ctx, string path,
    [MCPParam("...")] float? valueX = null,
    [MCPParam("...")] float? valueY = null)
{
    var current = /* read existing value */;
    if (valueX.HasValue) current.x = valueX.Value;
    if (valueY.HasValue) current.y = valueY.Value;
    /* write current back */
}
```

This is what `set_transform`, every Physics Vector3 setter, and
`set_rect_transform` all do — a caller can change just one axis without
having to first read back and resupply the others.

## §8. Path guarding — required for anything touching the filesystem

If your tool reads/writes/deletes a file under `Assets/`, route it through
`MCPPathGuard.TryResolveWithinAssets`:

```csharp
if (!MCPPathGuard.TryResolveWithinAssets(MCPProjectUtil.ProjectRoot, relativePath, out var fullPath, out var error))
    return MCPResult.Fail(error);
```

This confines the resolved path to the project's `Assets/` folder and rejects
traversal (`../`) and absolute paths. Every Scripting and Assets tool uses
this — don't hand-roll your own path validation.

## §9. Testing your tool without opening Unity

`dev-tests/csharp/` compiles the *real* production `.cs` files (yours
included, once you add it to `run_tests.sh`'s file list) against lightweight
Unity API stubs and runs actual behavioral checks — not a mock of your tool,
the real thing. This is how every bug mentioned in this project's changelog
was actually caught (a missing `MonoBehaviour` stub, an `Object` ambiguity, a
variable name collision) — before ever touching a real Editor.

To add your tool to this:
1. Add your new `.cs` file's path to the `mcs` command in
   `dev-tests/csharp/run_tests.sh`.
2. If your tool uses a Unity API not already stubbed (check
   `dev-tests/csharp/stubs/UnityStubs.cs` first), add a minimal stub for it —
   just enough surface area to compile, not a full simulation.
3. Add a test in `dev-tests/csharp/RegistryTests.cs` calling
   `MCPToolRegistry.Invoke("your_tool_name", args, ctx)` directly and
   asserting on the result. Every existing module has a `TestXTools(ctx)`
   method to use as a template.

**Honest limitation to know about:** because the stub doesn't simulate a real
scene graph, any tool that starts by resolving an existing GameObject via
`MCPSceneUtil.ResolvePath` can only have its *guard-clause* behavior verified
this way (path-not-found, missing-component, etc.), not a full "attach a real
component and read it back" success path. That's been true since Phase 1 and
isn't specific to your new tool — exercise the real success path once inside
an actual Unity Editor.

## §10. Checklist before you consider a new tool done

- [ ] `[MCPTool]` with a clear `name` and `description`
- [ ] Every parameter has an `[MCPParam]` unless its purpose is genuinely
      obvious from the name
- [ ] Enum parameters use a real C# `enum`, not a string with valid values
      only mentioned in prose
- [ ] Correct `latencyTier` (`Slow` if it can trigger a domain reload)
- [ ] `destructive: true` instead of a hand-rolled confirm check, if
      applicable
- [ ] A sensible `group` — `"core"` only if every session needs it
- [ ] Filesystem access goes through `MCPPathGuard`
- [ ] Added to `dev-tests/csharp/run_tests.sh` and has at least a
      guard-clause test in `RegistryTests.cs`
- [ ] `bash dev-tests/csharp/run_tests.sh` passes

## §11. Composite tools: chaining existing tools without new Unity-side logic

Everything above is for a genuinely new *atomic* tool — one that does
something no existing tool does, usually by calling a Unity API directly.
But a lot of what you'll want is really a **composite** tool: an existing
sequence of atomic tools you find yourself asking an agent to chain
together repeatedly ("create a GameObject, add a collider, set its
position..."). For that, write a Python `@workflow`-decorated function
directly in `python/unity_mcp_server/custom_workflows.py` — the exact same
mechanism `batch_execute` and `create_behavior_tree` are built on (see
`workflows.py` for the pattern). No restart of Unity needed; just reconnect
your MCP client session to pick up the new tool.

If what you need is "chain some tools together," write a composite tool in
Python; if you need new Unity-side logic no existing tool has, write a
`[MCPTool]` method as described above.
