# Unity MCP — Direct Config-File Writing + 4 Clients (Claude Code, Codex, Cursor, Antigravity)

Rolled back to the "security fixes" checkpoint, then rebuilt Configure from
scratch: **writes each client's config file directly instead of shelling out
to a CLI**, and adds two more clients (Cursor, Antigravity) whose schemas
were verified to be safe, low-risk additions.

## Why the redesign

The CLI-based approach (`claude mcp add` / `codex mcp add`) had a real,
reported failure mode: the CLI records a command for the *client* to spawn
later, from a working directory this project has no control over or
visibility into — which was the actual root cause of a `ModuleNotFoundError`
hit in production. Writing the config file directly removes that class of
failure entirely: every value (the absolute venv python path, `PYTHONPATH`,
`UNITY_MCP_PROJECT_ROOT`) is baked into the file itself. There's nothing left
to guess about at spawn time, and no dependency on the `claude`/`codex` CLIs
being installed at all.

## What was verified before writing any code

- **Claude Code**: `.mcp.json` at the project root, confirmed as current,
  documented, real behavior — `{"mcpServers": {"<name>": {"command", "args", "env"}}}`.
- **Cursor**: `.cursor/mcp.json`, confirmed identical schema across many
  independent sources, including explicit statements that Cursor uses "the
  same JSON format as Claude Desktop."
- **Antigravity** (Google's replacement for Gemini CLI, relaunched May 2026):
  same schema, `.agents/mcp_config.json`. This one's genuinely newer and
  faster-moving than the others — some inconsistency was found across
  sources for the exact file location as the product settled post-relaunch,
  used the most consistently-cited current convention, and flagged the
  uncertainty rather than overstate confidence.
- **Codex**: `.codex/config.toml`, TOML not JSON, with a real caveat —
  project-scoped config only loads for directories marked **trusted** in
  Codex. Surfaced in the Configure success message, not buried.
- **OpenCode was deliberately left out.** Its schema is genuinely different
  (`mcp` key not `mcpServers`, `command` as an array not string+args,
  `environment` not `env`), and a still-open GitHub issue requesting
  Claude-style `.mcp.json` support suggests that path isn't reliably there
  yet. Rather than ship something built on an unverified schema, this is
  documented as a gap, not silently skipped.

## The one real engineering decision: how to merge safely

JSON (Claude Code/Cursor/Antigravity) can be genuinely parsed and merged —
built a small, purpose-built JSON reader (`MCPMiniJson`) specifically for
this, rather than depend on the project's existing Newtonsoft.Json (whose
stub fake in the test environment is a hardcoded no-op, making real merge
behavior untestable there). It doesn't attempt to be a general parser — it
only finds where each existing entry's raw text starts and ends, so entries
for *other* tools are spliced back in byte-for-byte untouched, never fully
interpreted.

TOML (Codex) is harder to parse robustly without a real library, which
wasn't worth taking on as a dependency for one feature. Instead:
`MCPCodexTomlWriter` finds the bounded span of *just our own* generated
section (by table-header boundaries) and either appends it (first time) or
replaces that exact span in place (re-configuring) — every other table in
the file is left completely alone, verified with a test that puts another
section *after* ours specifically to prove the boundary-finding doesn't
overrun.

Both are **idempotent**: re-clicking Configure after changing the Python
server path updates the file in place rather than duplicating entries or
leaving stale values.

## Verified

185 checks (was 156 before this batch) — including real disk I/O: an actual
temp file with a pre-existing, unrelated server entry, written to, verified
on disk to preserve that entry while adding ours, then re-written with
different values and verified the file was actually updated in place — for
both the JSON and TOML paths.

## Setup

1. **Replace** your `com.unitymcp.bridge/Editor/` folder — `MCPClientConfigurator.cs`
   is gone; `MCPClientDetector.cs` is much smaller; five new files
   (`MCPMiniJson.cs`, `MCPMcpServersJsonWriter.cs`, `MCPCodexTomlWriter.cs`,
   `MCPServerEntryBuilder.cs`, `MCPMcpConfigTargets.cs`); `MCPSetupWindow.cs`
   rewritten.
2. Recompile — still **40 tool(s) registered**.
3. Open `Window → Unity MCP → Setup` — you'll now see four clients instead
   of two. Configure writes the file directly; no `claude`/`codex` binary
   needs to be installed at all anymore.
4. **Add each generated config file to `.gitignore`** rather than commit it —
   it contains an absolute, machine-specific path. Each teammate should run
   Configure once locally to generate their own.
5. For Codex specifically: if the config doesn't seem to load, check that
   the project directory is marked trusted in Codex — project-scoped config
   is silently ignored otherwise.

Phases 1-7 (core through streamable HTTP + UPM packaging) + multi-instance
conflict detection + **three of the six code-review findings: path-guard
symlink handling, deterministic/cached reflection type resolution, and a
hierarchy-cache invalidation gap that turned out to be reachable today, not
just theoretical.**

## Fix 1: Path-guard symlink traversal

### The gap

`MCPPathGuard`'s containment check was purely lexical — `Path.GetFullPath`
correctly collapses `..` segments, so simple traversal was already blocked,
but nothing checked whether any directory *along* the resolved path was
actually a symlink (or Windows junction) pointing somewhere else entirely. A
relative path that looks like it stays under `Assets/` could still resolve
to a real file outside it — e.g. if `Assets/Escape` were a symlink to `/etc`,
the string-prefix check would pass while the OS resolved the real write
wherever the symlink actually points.

### The fix

`TryCheckNoReparsePoints` walks the resolved path's real directory chain
(only the parts that already exist — a `create_*` tool's not-yet-existing
target can't itself be a symlink) and refuses if anything along the way,
including the final leaf, has the `FileAttributes.ReparsePoint` flag.

### Verified with an actual symlink, not a simulation

The test creates a **real symlink on disk** (`ln -s`) pointing outside a
temp `Assets/` folder, then confirms `MCPPathGuard` genuinely refuses to
traverse through it — both as an intermediate directory and as the final
path component — while a completely normal, symlink-free path still resolves
correctly (regression check). Cleanup explicitly removes the symlink itself
before any recursive directory delete runs, specifically to avoid the
cleanup routine accidentally deleting the *symlink target's* contents if the
local .NET/Mono runtime's recursive-delete happened to follow it. On a
system without `ln` available, this test skips the symlink-specific
assertions with a clear message rather than failing outright.

## Fix 2: Reflection type resolution — caching and deterministic ambiguity

### The gap

`MCPTypeResolver.Resolve()` re-scanned every loaded assembly on *every
single call*, with no caching. Worse: `AppDomain.CurrentDomain.GetAssemblies()`
has no guaranteed stable order, and the old code took the first type whose
*short* name matched, across every assembly. Two unrelated types sharing a
short name — very plausible, e.g. your own `Enemy` class versus a package's
`Enemy` class — could silently resolve to the wrong one, possibly
differently between calls, with no error or warning.

### The fix

- Results (both successes and failures) are now cached per type name,
  safely invalidated for free on every domain reload since it's plain
  static state.
- An exact `FullName` match is now preferred whenever available — inherently
  deterministic, since a fully-qualified name should be unique in a
  well-formed program.
- If resolution would otherwise be genuinely ambiguous, it now **fails
  loudly** with every candidate's full name listed, instead of silently
  picking one. All 5 call sites (`ComponentTools`'s 4, `AssetTools`'s 1) now
  surface this richer error via a new `TryResolve` method instead of the old
  `Resolve() → null` pattern.

### Verified with real ambiguity, not a hypothetical

The test defines two actual classes named `AmbiguousTestType` in different
namespaces, compiled into the test binary itself, and confirms: the short
name is refused with both full names listed; the same name fully qualified
resolves correctly; a genuinely nonexistent name still says "not found," not
"ambiguous"; and a normal unambiguous name still resolves correctly
(regression).

## Fix 3: Hierarchy-cache invalidation gap — confirmed reachable today

### The gap, and why it's more than theoretical

`MCPHierarchyCache` only invalidated on `EditorApplication.hierarchyChanged`,
which fires reliably for structural changes (create/destroy/reparent) but
*not* reliably for property mutations like renaming a GameObject. This was
flagged as a narrow, "manual Editor-UI edit only" limitation when the cache
was first built. It isn't: `Transform.name` is a real, settable property
proxying to `GameObject.name`, and the existing generic `set_component_field`
tool can already reach it —
`set_component_field(path, "Transform", "name", "NewName")` renames a
GameObject today, through a tool this project already ships, not just
through a human clicking around in the Editor.

### The fix

Also subscribes to `ObjectChangeEvents.changesPublished` (Unity 2021.1+,
under this package's declared minimum of 2021.3), which covers a superset
of changes including property edits and renames. Any published change at
all invalidates unconditionally — deliberately not trying to filter down to
exactly the change types that matter, since that kind of narrowing is
exactly what produced the original gap.

### Verified

Confirms a published change with `length > 0` invalidates on its own (no
`hierarchyChanged` needed), a published event with `length == 0` does *not*
invalidate (matching the code's explicit guard), and the pre-existing
`hierarchyChanged` path still works (regression). One real bug surfaced
*while building this test*: it initially assumed a cold cache, but
`MCPHierarchyCache` is shared static state already exercised by an earlier
test in the same run — fixed by forcing a known starting state and measuring
build-count deltas instead of absolute counts.

## Verifying it

```bash
bash dev-tests/csharp/run_tests.sh
```
156 checks now (was 141) — including a real filesystem symlink, two real
ambiguous types compiled into the test binary, and genuine cache-invalidation
delta tracking.

## Setup

1. **Replace** your `com.unitymcp.bridge/Editor/` folder —
   `MCPPathGuard.cs`, `MCPTypeResolver.cs`, `MCPHierarchyCache.cs`,
   `ComponentTools.cs`, and `AssetTools.cs` all changed.
2. Recompile — still **40 tool(s) registered** (these are all internal
   correctness fixes, no new tools).
3. No behavior change for any normal, non-symlinked, non-ambiguous usage —
   these fixes only change behavior in the specific edge cases they close.

## Still open from the same review

Three items left: the Python server management GUI, the tool-groups GUI, and
the bigger architectural pair (main-thread blocking + no async job pattern
for long-running actions) — the highest-value and largest remaining piece.
Unencrypted socket transport is also still open, lower urgency given the
loopback-only bind already limits its blast radius.

Phases 1-7 (core through streamable HTTP + UPM packaging) + **multi-instance
port/session conflict detection** — the first of several real issues found
through actual production use and a follow-up code review, tackled one at a
time starting with the one that directly explained a reported symptom.

## Multi-instance conflict detection: root cause and fix for "port detection isn't intuitive"

### The reported symptom

"Sometimes handshake happens on some other port and unity project is
registered on other, in that case the AI agent does not get the MCP tools
and the setup window shows 0 clients."

### The actual mechanism (verified against the real code, not assumed)

The Setup window's "Running on port X" reads `MCPServer.BoundPort` — a live,
in-memory value belonging to *that specific Unity process*. `diagnose_bridge.py`
and the AI agent both instead read `Library/MCP/session.json` from disk. Under
normal single-instance operation these always agree, because the same
`Start()` call writes both.

They stop agreeing the moment **a second live Unity process for the same
project exists** — a genuinely orphaned instance from a crash, or literally
two Editor windows open on one project. Each instance independently searches
for and binds its own free port (by design, since Phase 2), and each one
**overwrites `session.json`** on every domain reload. The Setup window you're
looking at keeps showing *its own* process's port, unaffected — while an MCP
client reads whichever process most recently won the race to write
`session.json`, which might be the other one entirely. That also explains
"0 clients": the agent may be talking to a different Unity process than the
one whose Setup window you have open.

Unity is the only vantage point able to catch this class of problem — it's
the only place with access to *both* truths at once (its own live in-memory
state, and whatever's currently on disk, which might belong to someone else).
A Python-side diagnostic only ever sees the disk file, which is exactly the
thing that can be misleading during a live conflict.

### The fix

- `MCPSessionFile.TryReadCurrent()` — reads whatever's on disk *right now*
  without assuming it's this process's own, returning false (not an
  exception) for any transient/malformed read rather than treating that as
  an error.
- `MCPInstanceConflictDetector.Evaluate()` — pure decision logic, with the
  actual "is this PID alive" check passed in as a delegate so it's fully
  unit-testable with fake alive/dead process states. Distinguishes: no file
  at all (fine), file belongs to this process (fine), file belongs to a
  now-dead PID (stale, harmless), file's port matches this process's own
  (impossible for two real listeners — treated as a transient mid-write
  read, not a second listener), and the actual incident: a different, live
  PID on a genuinely different port — flagged, with the specific PID and
  port named in the message, not a generic "conflict exists."
- `MCPServer.Start()` now runs this check against whatever's on disk
  *before* overwriting it, logging a clear Console warning the moment a real
  conflict is detected — proactive, not just discoverable by opening a window.
- The Setup window now shows live conflict status, auto-refreshing every 2
  seconds while open (via the standard `EditorApplication.update`-driven
  repaint pattern, so it updates even with zero user interaction) — a
  prominent warning box naming the other PID/port/start-time when there's a
  real conflict, or a quiet confirmation when there isn't.

### Verified

10 new checks in the C# suite cover every branch of `Evaluate()` directly —
including confirming the `isProcessAlive` delegate is actually consulted with
the correct PID (not ignored or hardcoded), and a smoke test that the real
OS-backed wrapper (`DetectReal()`) runs against actual process state without
throwing.

## Setup

1. **Replace** your `com.unitymcp.bridge/Editor/` folder — adds
   `MCPInstanceConflictDetector.cs`; `MCPSessionFile.cs`, `MCPServer.cs`, and
   `MCPSetupWindow.cs` all changed.
2. Recompile — Console should now additionally report a conflict warning
   immediately if one exists, not just quietly misbehave.
3. Open `Window → Unity MCP → Setup` — you should see a new status line right
   under Bridge Status confirming no conflict, or a clear warning naming the
   specific other process if one exists.
4. If you've hit this before: the next time you see it, the warning should
   tell you exactly which PID to close instead of leaving you to guess.

## What's still on the list from the same review

This was the first of nine items raised together (three feature requests,
six code-level findings) — tackled first since it directly explained a
reported bug. Still open: the Python server management GUI, a tool-groups
GUI, and the security/architecture findings (path-guard symlink handling,
unencrypted socket transport, main-thread blocking on long operations, no
async job pattern for genuinely long-running tools, non-deterministic/
uncached reflection type resolution, and hierarchy-cache invalidation gaps
including one that's reachable today via the existing `set_component_field`
tool, not just a theoretical manual-Editor-edit case).

Phases 1-6 (core through the visual Tool Builder) + the reconnect-notification
fix + **Phase 7: streamable-HTTP transport and real UPM packaging** — this is
the current state of the project. Everything below "## Current setup" is
historical dev log, kept for context on how each piece was built and verified,
but the paths and instructions in it are **superseded** by the structure
described first.

## Bug fix: reported symptom — Generate button → connection lost → port changed → core tools drop from 17 to 3

### What was actually happening (confirmed, not guessed)

Two separate things were going on, and only one of them was actually a bug:

**1. The tool-count drop was correct behavior for a genuinely disconnected
bridge — not itself a bug.** While Unity is unreachable, `bridge.list_tools()`
fails, `server.py`'s `list_tools()` handler catches that and returns an empty
Unity tool list, so only Python-side workflow tools in currently-active
groups remain visible — by default `batch_execute` and `manage_tools`, plus
whatever else was just generated. This is confirmed directly in the new
regression test as the expected degraded state *during* an outage.

**2. The actual bug: nothing ever told the client to look again once Unity
came back on the new port.** The connection layer already handled a changed
port correctly (`session.json` is re-read fresh on every reconnect, per the
Phase 2 fix) — but MCP clients cache `list_tools()` and don't refetch on
their own. A client mid-session when this happened stayed stuck at the
degraded list forever, even after Unity fully recovered. This is the exact
same class of bug as the earlier `manage_tools` notification fix — just
triggered by a disconnect/reconnect instead of a group change, and nothing
was handling this trigger yet.

**A second, more fundamental bug was found while fixing the first one:**
`UnityBridgeClient.connect()` didn't wrap raw socket failures
(`ConnectionRefusedError`, etc.) into `BridgeError` — so while Unity was
actually unreachable, the exception would escape every caller's
`except BridgeError` uncaught and **crash the entire MCP call** instead of
degrading gracefully to the reduced tool list described above. This was
caught by the *regression test itself* failing with an unhandled
`McpError` on its very first run — a good example of a test built to verify
one fix surfacing a second, more serious bug along the way.

### The fixes

- `bridge_client.py`: `UnityBridgeClient.add_reconnect_listener()` — fires
  registered async callbacks whenever a connection is re-established after
  having been lost (not on the very first connect). `server.py` registers a
  listener that sends the same `tools/list_changed` notification
  `manage_tools` already used, extracted into a shared `_notify_tools_changed()`
  helper.
- `bridge_client.py`: `connect()` now wraps both `security.read_session()`
  (can raise `FileNotFoundError`/`ValueError` for a missing/inconsistent
  `session.json`) and `asyncio.open_connection()` (can raise any `OSError`)
  into `BridgeError`, so every failure mode is the one type every caller
  actually catches.

### On the (still not fully confirmed) root cause of *why* the port changed

Since nothing in `MCPToolBuilderWindow.GenerateAndAppend()` calls any Unity
API at all — it's plain `System.IO` file writing to an external Python file
— the most plausible explanation is that the configured **Python server
location was nested inside the Unity project** (e.g. the whole repo copied
into `Assets/` instead of just `Assets/Editor/MCP/`), so the file write
landed somewhere Unity's own asset-watching machinery could notice and
trigger an unrelated reimport/recompile pass. This can't be fully confirmed
without a Console log from the actual incident, so a defensive fix ships
either way:

- `MCPToolBuilderSettings.IsInsideProject()` — pure path-containment check.
  `MCPToolBuilderWindow` now refuses to generate (with a clear explanation)
  if the configured Python server location is the same as, or nested inside,
  the Unity project root.

### Verified with a real regression test, not just described

`test_reconnect_notifies_client.py` runs a **real `ClientSession`** (same
in-memory-transport technique as the earlier `manage_tools` fix) through the
entire incident: connect with the full tool set visible → simulate Unity's
old listener disconnecting → confirm the degraded-but-correct 2-tool state →
simulate Unity restarting on a genuinely different port → confirm the bridge
transparently reconnects to the new port → confirm a real
`ToolListChangedNotification` is actually received by the client → confirm a
refetch afterward shows the full tool set again. Every step is a real
assertion against real behavior, not a description of intended behavior.

## Current setup

The project is now packaged as a proper UPM package:

```
unity-mcp-phase1/
  unity/com.unitymcp.bridge/    # the actual UPM package — install this
    package.json
    README.md                   # end-user docs — read this first if you're new
    CHANGELOG.md
    LICENSE.md                  # placeholder — no license chosen yet
    Editor/                     # all C# source (was Assets/Editor/MCP/)
  python/                       # the MCP server Claude Code / Codex talk to
  docs/writing-custom-tools.md  # custom-tool authoring guide
  dev-tests/csharp/             # runnable C# logic tests, no Unity required
```

1. **Install the package**: `Window → Package Manager → + → Add package from
   disk...`, point at `unity/com.unitymcp.bridge/package.json`. (Or "Add
   package from git URL" if this is hosted in a repo.) This replaces the old
   "copy `Assets/Editor/MCP/` into your project" instructions from every
   earlier phase — if you have that old folder from a previous version,
   remove it first so you don't end up with two copies of the bridge.
2. Newtonsoft Json is now an auto-resolved package dependency
   (`com.unity.nuget.newtonsoft-json`) — you shouldn't need to add it
   manually anymore.
3. Python side unchanged: `cd python && python3 -m venv .venv && pip install
   -r requirements.txt`.
4. Recompile — Console should report **40 tool(s) registered**.

### New in Phase 7: streamable HTTP

An alternative to stdio for driving Unity from a different machine, or
sharing one running server process across multiple client connections.
**Not needed for normal local Claude Code / Codex usage** — stdio (the
default, everything from Phase 1 on) already covers that.

```bash
UNITY_MCP_TRANSPORT=http python3 -m unity_mcp_server.server
```

Defaults to `127.0.0.1:8765`. Binding beyond loopback
(`UNITY_MCP_HTTP_HOST=0.0.0.0` or similar) is **refused outright at startup**
unless `UNITY_MCP_HTTP_TOKEN` is also set — an HTTP listener is a materially
different trust boundary than stdio (reachable by any local process, not just
the one that spawned it), so this isn't just a documented recommendation,
it's enforced in code. When set, every request needs a matching
`Authorization: Bearer <token>` header or gets a real 401.

Verified with a genuine end-to-end test (`test_streamable_http_transport.py`)
— an actual `uvicorn` server, an actual HTTP client, actual sockets: a
real `ClientSession` initializes over real streamable HTTP, `list_tools()`
and `call_tool()` both work and actually reach the (fake, for the test)
Unity bridge, and a request with a missing or wrong token gets a genuine
401 — not simulated behavior.

**A subtlety worth knowing if you ever build on this:** the MCP SDK's
`StreamableHTTPSessionManager` calls `server.create_initialization_options()`
internally with no arguments, which would silently default to
`NotificationOptions()` (every capability `False`) — meaning the
reconnect-notification fix from the previous batch would have silently
stopped working for HTTP clients specifically, with no error, nothing to
notice until someone using HTTP hit the exact bug that was just fixed for
stdio. Fixed by overriding `create_initialization_options` at the instance
level once, in `server.py`, so both transports share one source of truth for
notification capability defaults instead of two call sites that could drift
out of sync.

### Verifying it

```bash
bash dev-tests/csharp/run_tests.sh                          # unaffected by the Python-only Phase 7 changes
python3 dev-tests/verify_bt_framework_compiles.py
cd python && python3 tests/test_streamable_http_transport.py  # the real HTTP test described above
```

The full restructure (`Assets/Editor/MCP/` → `com.unitymcp.bridge/Editor/`)
was re-verified by actually rerunning the complete C# test suite from the new
location — not assumed safe because "it's just a file move."

## Everything from here down is historical dev log



Phases 1-5 (core bridge, security/registry, 40 atomic tools, performance,
composite/Behavior-Tree tools) + Tool Groups + custom-tool authoring polish +
a real bug fix (group activation notification) + the Setup window +
**Phase 6: the visual Tool Builder — a real "tools that build tools" UI,
closing the last of this project's original goals.**

```
unity-mcp-phase1/
  unity/Assets/Editor/MCP/            # drop this folder into your Unity project
  unity/Assets/Editor/MCP/Setup/      # the Setup window (client config UI)
  unity/Assets/Editor/MCP/ToolBuilder/ # NEW: the visual Tool Builder
  python/                             # the MCP server Claude Code / Codex talk to
  python/unity_mcp_server/custom_workflows.py  # NEW: where the Tool Builder writes generated tools
  dev-tests/csharp/                   # runnable C# logic tests, no Unity required
  dev-tests/verify_bt_framework_compiles.py   # compiles the real BT framework content
  docs/writing-custom-tools.md        # the custom-tool authoring guide (now covers both paths)
```

## History: Phase 6, the visual Tool Builder (built the batch before this fix)

`Window → Unity MCP → Tool Builder` — build a new composite tool by chaining
existing atomic tools together, entirely from a form. No C# and no
hand-written Python required from here on for "I keep asking an agent to do
these three tools in a row" — this is the actual "tools that build tools"
endpoint from the very first ask in this conversation.

### How it works

Pick a chain of existing tools (any of the 40 atomic tools, or an existing
composite tool), wire each step's arguments — a literal value, `{paramName}`
to reference one of the new tool's own parameters, or `{stepN.field}` to pull
a field out of an earlier step's result — name it, click Generate. That
writes a real `@workflow`-decorated Python function into a new file,
`custom_workflows.py`, using the exact same registration mechanism
`batch_execute` and `create_behavior_tree` already use. The builder only ever
**appends** — a function you've since hand-edited is never touched or
overwritten by a later generation.

### Validation happens against the LIVE tool registry, not a static list

This is the part that only makes sense running inside Unity: every step is
checked against `MCPToolRegistry` as it actually exists right now — is this a
real tool, does it really have this parameter, is a required parameter
actually being supplied, does a `{stepN.field}` reference actually point
backward in the chain (not to itself or a later step). A destructive step
(e.g. `delete_gameobject`) gets `confirm: true` appended automatically by the
generator — it's never a fillable field in the builder UI, so a composite
tool built around a destructive step can't accidentally ship as a silent
no-op.

### Verified by actually closing the loop, not just describing it

This is the one feature in the whole project where full verification was
genuinely possible: the C# generator's test (`TestCompositeToolGenerator` in
`RegistryTests.cs`) prints the *exact* Python source it produces for a real
3-step spec (create → move → delete, chaining `create_gameobject` →
`set_transform` → `delete_gameobject`), and a companion Python test
(`test_tool_builder_generated_code.py`) takes that exact byte-for-byte output
— copied verbatim, not retyped — and actually executes it against the fake
bridge. It checks real side effects, not just `ok: true`: the generated
tool's GameObject genuinely gets created, genuinely moves to the position
specified, and genuinely gets deleted, in that order, and does so correctly
for a second, differently-named object too — proving the `{objName}`
parameter binding isn't a fluke that happened to work once.

Alongside the happy path, 9 distinct validation-failure modes are each
checked to name the *specific* problem (bad name, duplicate name, empty
description, zero steps, unknown tool, unknown argument, missing required
argument, undeclared parameter reference, and a step referencing itself or a
later step) — not a generic "invalid spec" for all of them.

### A real design correction made before writing any code

The original plan assumed the Python server always lives at a fixed relative
path from the Unity project (`../python/unity_mcp_server/`) — true only for
*this repository's own* dev layout, where `unity/` and `python/` happen to be
siblings. In a real deployment, a user copies `Assets/Editor/MCP/` into an
arbitrary Unity project with no guaranteed relationship to where they've put
the Python server on disk. Fixed with `MCPToolBuilderSettings`, an
`EditorPrefs`-backed setting (with a Browse button) instead of a baked-in
assumption — caught during design, not after shipping something broken.

### What's tested, and what genuinely needs a real Editor

Generator logic — validation and code generation — is fully unit-tested, plus
the full-loop execution test described above. **Not** independently
verifiable outside a real Editor: the `EditorWindow` UI rendering itself, and
`EditorPrefs`/`OpenFolderPanel`'s real OS-level behavior (the stub versions
are just enough to compile against, same honest boundary as the Setup
window's actual process-spawning).

### Setup steps for Phase 6 itself (already applied if you're current)

1. **Replace** your `Assets/Editor/MCP/` folder — adds the new `ToolBuilder/`
   subfolder (`MCPCompositeToolSpec.cs`, `MCPCompositeToolGenerator.cs`,
   `MCPToolBuilderSettings.cs`, `MCPToolBuilderWindow.cs`).
2. **Add `custom_workflows.py`** to your `python/unity_mcp_server/` folder —
   this is what the builder appends generated tools to. `workflows.py` was
   also updated (one import line, at the very end) to load it automatically.
3. Let Unity recompile — Console should still report **40 tool(s)
   registered** (the Tool Builder is Editor tooling, not an MCP tool itself).
4. Open `Window → Unity MCP → Tool Builder`, set the Python server location
   at the top (Browse to your `unity_mcp_server` folder), then try building
   something small — e.g. a `spawn_and_configure` tool chaining
   `create_gameobject` → `add_component` → `set_component_field` — to see
   the whole loop for yourself.
5. Restart your MCP client session after generating a tool to pick it up.

## History: the Setup window (built the batch before this one)

`Window → Unity MCP → Setup` — replaces every terminal command from Phase 1's
setup instructions with an actual UI.

**Bridge status**, read live from `MCPServer`: whether it's running, on which
port, and — new — a connected-client count (`MCPServer.ConnectedClientCount`,
incremented on a successful handshake and decremented when the connection
ends). A listening port alone doesn't tell you anything actually connected to
it; this does.

**Per-client configuration**, for Claude Code and Codex: a "Check status"
button and a "Configure" button, each running the relevant CLI command and
reporting the result inline — no more copy-pasting `claude mcp add ...` by
hand.

### Two real things I checked before writing any of this, not assumed

1. **Claude Code's `claude mcp add` defaults to *local* scope** — tied to
   whatever directory the command runs from. The window runs it with the
   Unity project as the working directory, so this lines up naturally: no
   `--scope` flag needed, "local" just means "this project" by construction.
2. **Codex's `codex mcp add` always writes to the global
   `~/.codex/config.toml`** — there's no project-scope flag. A plain `"unity"`
   server name would collide across multiple Unity projects on one machine.
   Both clients register under a project-derived name instead
   (`unity-<ProjectFolderName>`, sanitized), avoiding the collision entirely
   rather than working around it after the fact.

### A practical gotcha this is built to route around

Unity launched from Finder/Dock (not a terminal) often inherits a **minimal
PATH** that doesn't include nvm/homebrew locations — so a naive
`Process.Start("claude", ...)` can fail with "command not found" even though
`claude` works fine in your actual terminal. On macOS/Linux, commands run
through a login shell (`zsh -lc "..."`) specifically so `.zshrc`/`.bashrc` get
sourced the same way a real terminal session would. Windows doesn't have this
problem as consistently, so `cmd.exe /c` is enough there.

### What's tested, and an honest line about what isn't

Everything **except actual process spawning** is unit-tested: `ServerName`'s
sanitization (including edge cases — empty, null, special characters),
`BuildListCommand`/`BuildAddCommand`'s exact generated command strings for
both clients, `IsRegistered`'s output parsing against realistic sample output
from both clients (including a same-line JSON case, a spaced vs. compact JSON
case, and — worth calling out specifically — a check that a *longer* name
sharing a prefix, e.g. `unity-MyGame2`, does **not** falsely match
`unity-MyGame`), and `BuildStartInfo`'s shell-wrapping decision for both
platforms via an injectable `isWindows` parameter rather than relying on the
actual OS the test happens to run on. 20 new checks, all real logic, none of
it mocked.

**What genuinely can't be verified this way, stated plainly:** actually
spawning `claude`/`codex`, real PATH resolution, and the `EditorWindow`
rendering itself. That needs a real OS and a real Editor — exactly the
boundary named in the plan before building any of this.

### One real bug the compile-check step caught immediately

My stub had declared `EditorWindow.OnGUI()` as a `virtual` method requiring
`override` — real Unity doesn't work that way. Like `MonoBehaviour.Start()`/
`Update()`, `OnGUI()` is a "magic method" the Editor discovers and calls via
its own message-dispatch convention, not C# virtual dispatch — you declare
`void OnGUI()` in your subclass with no `override` keyword. The actual
`MCPSetupWindow.cs` was written correctly from the start (matching real
Unity's convention); the stub was wrong and got fixed, the same way the
`MonoBehaviour`/`Component.name`/`Time.deltaTime` gaps did back in Phase 5.

### Setup steps for the Setup window itself (already applied if you're current)

1. **Replace** your `Assets/Editor/MCP/` folder — adds the new `Setup/`
   subfolder (`MCPClientDetector.cs`, `MCPClientConfigurator.cs`,
   `MCPSetupWindow.cs`); `MCPServer.cs` changed (connected-client counter).
2. Let Unity recompile — Console should still report **40 tool(s)
   registered** (the Setup window is Editor tooling, not an MCP tool, so this
   number doesn't move).
3. Open `Window → Unity MCP → Setup`. You should see live bridge status
   immediately; try "Check status" for Claude Code / Codex before hitting
   Configure, so you can see the before/after.
4. No Python changes in this batch.

## History: everything built before that

Condensed here rather than repeated in full — each item below was its own
detailed write-up when originally built; this is the settled summary.

**Bug fix (previous batch) — `manage_tools activate` wasn't reaching the
client.** Root cause: MCP clients cache `list_tools()` at session start and
don't auto-refetch — the server has to explicitly send a
`notifications/tools/list_changed` notification, and this server did neither
half of that (didn't declare the `tools_changed` capability, didn't send the
notification). Fixed in `server.py`. Verified with a **real** `ClientSession`
over the MCP SDK's in-memory transport (`test_group_activation_notifies_client.py`)
— not the usual direct-function-call test style, which is exactly why this
bug shipped past a green suite in the first place: that style bypasses the
real MCP request context entirely.

**Custom-tool authoring polish.** `[MCPParam]` for per-parameter schema
descriptions (applied across all 40 existing tools, not just the mechanism);
enum-typed parameters now get a real `"enum": [...]` schema constraint
instead of bare `"type": "string"`; `docs/writing-custom-tools.md` as the
complete reference, with its own example verified to actually run, not just
read plausibly.

**Tool Groups.** 6 groups (`core` always on; `scripting`/`physics`/`assets`/
`ui`/`behavior_tree` toggleable), a `manage_tools` workflow tool
(`list_groups`/`activate`/`deactivate`/`reset`). A visibility mechanism, not
a security boundary — every real safety check (destructive gate, path guard,
rate limiter) applies regardless of group state.

**Phase 5 — composite tools.** The workflow-tool registry pattern
(`workflows.py`), with `batch_execute` as the first entry and a real, working
custom Behavior Tree framework (`Sequence`/`Selector`/`ActionNode`/`BTRunner`)
as proof of the pattern — generated into a project via the existing
`create_script`/`update_script` atomic tools, no C# changes required to build
the whole composite layer.

**Phase 4 — performance.** `batch_execute` (real wire-level batching, not a
Python-side loop), a fast/slow priority queue (verified with an actual
ordering test, not just "did it eventually run"), and `get_hierarchy` caching.

**Phase 3 — breadth.** 40 atomic tools total: Scene/Component/Query (15,
Phase 1), Scripting (6), Physics (6), Assets (7), UI (6).

**Phase 2 — registry & security**, including the **port/token incident
fix**: `MCPServer.cs` self-selects a free port and publishes `(port, token)`
together via `Library/MCP/session.json`, re-read fresh on every reconnect —
so multiple Unity processes on one machine can never cause a client to talk
to the wrong project's listener. If you ever hit a rejected handshake, run
`python3 diagnose_bridge.py /path/to/YourUnityProject` before reaching for
`netstat`. Also: centralized destructive/confirm gate, audit log
(`Library/MCP/audit.log`), rate limiting, path guard.

**Phase 1 — core.** TCP bridge, main-thread-only dispatch via
`EditorApplication.update`, stdio MCP server, handshake auth.

## What's next

Every phase from the original roadmap (1 through 7) plus all three
CoplayDev-alignment workstreams are now done, and the stack has been run in a
real Unity Editor, not just against stubs. What's genuinely left:

- **Editor-only code stripping** — already true since Phase 1 (the
  `.asmdef`'s `includePlatforms: ["Editor"]` means none of this ships in a
  player build), now more explicit given the real UPM package boundary.
  Worth a final confirmation with an actual player build if you want to be
  fully certain, but nothing in the design suggests otherwise.
- **A real license decision** — `LICENSE.md` is a placeholder. This is a
  choice for you to make, not something I should assume.
- **Publishing** — pushing `com.unitymcp.bridge/` somewhere `package.json`'s
  `documentationUrl`/`changelogUrl`/git-URL install can actually point to.
- **Tool breadth beyond 40** — the four Phase 3 modules covered the core of
  each area on purpose; there's room to go deeper (Animation, Terrain,
  NavMesh, more Physics queries) whenever it's useful.
- **More real-Unity mileage** — the one real-world bug report so far found
  two genuine issues the stub-based suite couldn't catch on its own. That's
  the value of actually using this day to day — worth continuing, and worth
  reporting anything else that looks off with specific observed symptoms,
  which is what made that fix possible to pin down precisely.
