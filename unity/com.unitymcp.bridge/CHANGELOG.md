# Changelog

All notable changes to this package are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows
[Semantic Versioning](https://semver.org/).

## [1.27.1] — Fix a real compile error blocking the package on current Unity versions

`get_render_stats` (`ProfilingTools.cs`) referenced `UnityStats.batches`, which doesn't
exist -- `UnityStats` only ever exposed the per-technique counts. This was a hard CS0117
compile error, so it broke the whole `UnityMCP.Editor` assembly (every tool, not just
profiling) regardless of install method -- Package Manager "Add from disk" and "Add from
git URL" both pull the same source, so both looked "broken" for the same underlying
reason.

### Fixed
- `get_render_stats`'s `batches` now sums `UnityStats.dynamicBatches +
  UnityStats.staticBatches + UnityStats.instancedBatches` (verified against
  `UnityStats.bindings.cs`), fixing the CS0117 error.
- Cleaned up the CS0618 obsolete-API warnings the Console also showed:
  `find_gameobjects` now uses `Object.FindObjectsByType` instead of the obsolete
  `FindObjectsOfType`; `UITools.EnsureEventSystem` now uses `FindFirstObjectByType`;
  `create_offmesh_link` and `mark_navmesh_area` wrap their still-necessary-but-obsolete
  `OffMeshLink`/`GameObjectUtility.SetNavMeshArea` calls in `#pragma warning disable
  CS0618` (same pattern already used elsewhere in this codebase for
  `LightingTools.cs`/`MCPTestRunnerCache.cs`), since the non-obsolete replacements live in
  the optional `com.unity.ai.navigation` package, which this package intentionally
  doesn't depend on.

## [1.27.0] — Tool Groups window: simplified descriptions, at-a-glance counters, live active state

Usability pass on the Tool Groups window based on real feedback: the AI-facing tool
descriptions shown there were accurate but written for an AI's search index
(docs/tool-scaling-strategy.md), not for a human skimming a settings panel, and the
window had no way to know a group the AI activated mid-session was actually active.

### Added
- Every tool/group description in the window is now run through a new
  `SimplifyDescription` step: keeps the first plain-language sentence or two, drops
  everything after the first " -- "/" — " aside (reliably where "what it does" ends and
  "how/why" begins in this codebase's description style), and hard-truncates on a word
  boundary past ~150 chars. Guards against the naive sentence-split falsely triggering on
  embedded abbreviations ("e.g.", "i.e.", "etc.") -- found by spot-checking real
  descriptions (`capture_editor_window`'s would have been cut off mid-parenthetical
  otherwise) before trusting it, not assumed to be correct in advance.
- A counters row at the top of the window: **Active groups** (N / total), **Active
  tools** (N / total), **Total groups**, **Total tools** -- computed from the same live
  vs. default active-state logic each group row already uses, so the summary line never
  contradicts the badges below it.
- **Live active state**, fixing a real reported gap: activating a group via `manage_tools`
  mid-session previously only ever showed as "Active (default)"/"Inactive (default)" in
  this window, which reflects the default for a *new* session, not what a live one
  actually did. New `Library/MCP/live_tool_state.json` (`groups.py`'s `_write_live_state`,
  called from `activate`/`deactivate`/`reset` plus once at process start) and
  `MCPLiveStateReader.cs` (Unity side, same mtime-cache pattern as
  `MCPToolManifestReader`) give the window the real answer: group rows now show "Active
  now"/"Inactive now" once a live session has reported in, with a secondary line noting
  when that differs from the session-default setting. Falls back to the old
  default-only display (labeled as such) until a Python server has run at least once.
  The window now also polls for external file changes on a ~1s timer
  (`EditorApplication.update`) and auto-repaints, so this no longer requires manually
  clicking Refresh to notice.
- `test_live_tool_state.py`: confirms the live-state file is written on reset/activate/
  deactivate with the real active set, and that a disabled group is never reported as
  live-active even if the in-memory state is stale.

### Note
- If more than one AI client session is connected to the same project at once, each
  writes its own process's state to the same file, so the window can only show the most
  recently updated session's live state -- a known, accepted limitation for a
  human-facing status display, not a correctness issue for the underlying group
  mechanism itself (each session still fully controls its own real active-group set
  independently; only this one dashboard's picture of it is single-writer).

## [1.26.0] — Tool scaling, phase 6: manage_packages source allowlist

Phase 6 (final phase) of `docs/tool-scaling-strategy.md` -- the plan's own "secondary,
lower-priority hardening" item. Pure C#, defaults to today's fully permissive behavior
so nobody is surprised by this unless they explicitly opt in.

### Added
- New `packageAllowlist` field in `Library/MCP/tool_groups_config.json`
  (`MCPToolGroupConfig.PackageAllowlist`/`SetPackageAllowlist`/`IsPackageAllowed`):
  exact package IDs or `"prefix.*"` wildcards (e.g. `"com.unity.*"`) that
  `manage_packages(action:"add")` is allowed to install. Empty (the default) means
  unrestricted, matching every prior version's behavior. Checked against the packageId
  with any `@version` suffix stripped first.
- A "Package allowlist" text field + Apply button in the Tool Groups window's header,
  writing the same config file the rest of the window already controls.
- `manage_packages`'s `add` case now refuses a non-allowlisted package with a clear
  message pointing at the Tool Groups window, before ever calling the real
  `Client.Add` -- `list`/`search`/`remove` are unaffected (an allowlist restricts what
  gets *added* to the project, not what can be listed/searched/removed).

This closes out the plan's 6-phase rollout (`docs/tool-scaling-strategy.md` section 10)
-- see that document's "Success metrics" section for how to tell it's working in
practice.

## [1.25.0] — Tool scaling, phase 5: Read-Only Mode

Phase 5 of `docs/tool-scaling-strategy.md`. The one genuinely new security capability in
the whole plan: a project-wide "look but don't touch" switch, built almost entirely by
reusing the disabled-groups plumbing (same config file, same Unity-writes/Python-reads
split, same immediate-effect-on-live-sessions behavior) now that phase 4's
`readOnlyHint` data exists to enforce against.

### Added
- **Read-Only Mode** toggle in `Window → Unity MCP → Tool Groups`'s header: when on,
  every tool call whose `readOnly`/`read_only` flag isn't literally `true` is refused,
  for every connected AI client, immediately (including a session already mid-
  conversation) -- not just for new sessions. Requires an explicit confirmation dialog
  to turn on; turning off needs none. Each tool row in the window is tagged "RO" when
  it's one of the tools this mode would still allow.
- New `readOnlyMode` key in `Library/MCP/tool_groups_config.json` (same file
  `disabledGroups`/`defaultActiveGroups` already live in) -- `MCPToolGroupConfig.
  IsReadOnlyMode()`/`SetReadOnlyMode()` on the Unity side, `groups.is_read_only_mode()`
  on the Python side, re-read on the same ~1s throttle as disabled groups.
- Enforcement at both independent points a tool call can originate from, same as
  disabled groups: `MCPToolRegistry.Invoke` (atomic tools, C#) and `server.py`'s
  `call_tool()` (composite/workflow tools, Python) -- unlike a disabled group's refusal,
  this one states its reason explicitly ("... is not read-only, and Read-Only Mode is
  enabled...") rather than mimicking "unknown tool", since Read-Only Mode isn't about
  hiding a tool's existence.
- `tool_manifest.json` (`server.py`'s `_write_tool_manifest`) and the wire protocol
  (`MCPToolDescriptor`) both now also carry `destructive`/`read_only` per composite
  tool, so the Tool Groups window can show the same "RO" tag for composite tools that it
  already could for atomic ones.
- `test_read_only_mode.py`: confirms the mode defaults off, a mutating composite tool
  works normally with it off, is refused by name with an explicit message once it's on,
  and normal behavior returns once it's off again.

### Note
- Coarse-grained by design for this phase: `manage_tools` itself is marked
  non-read-only as a whole (it has actions -- `activate`/`deactivate` -- that do mutate
  session state), so enabling Read-Only Mode also blocks calling `manage_tools`,
  including its otherwise-harmless `search`/`list_groups` actions. Splitting
  `readOnlyHint` to per-action granularity for a single multi-action tool isn't
  supported by the MCP spec's tool-level annotation model and wasn't worth a bespoke
  mechanism for this phase.

## [1.24.0] — Tool scaling, phase 4: MCP tool annotations (destructiveHint/readOnlyHint/openWorldHint)

Phase 4 of `docs/tool-scaling-strategy.md`. Wires the MCP spec's standard tool
annotations into every `list_tools()` response, so any spec-compliant client can make
risk-based decisions (e.g. skip a confirmation prompt for a read-only call) without
this project inventing its own out-of-band signal for it.

### Added
- `[MCPTool(..., readOnly: true)]` (new opt-in attribute param, `MCPToolAttribute.cs`):
  the C# side of `readOnlyHint`. Hand-verified per tool against its actual method body,
  not guessed from a `get_`/`list_` name prefix -- 40 of 42 naming-convention candidates
  were confirmed genuinely side-effect-free and marked; `get_frame_debugger_info` and
  `capture_profiler_frames` were deliberately left `false` despite their names, because
  both flip a global Profiler/FrameDebugger recording toggle as a side effect.
- `@workflow(..., destructive=..., read_only=...)` (new optional params, `workflows.py`):
  the same two hints for composite tools. `replace_prefab_instances` is the one composite
  marked `destructive=True` (it removes existing prefab instances in the scene, not just
  scratch state the tool created itself); everything else defaults `False` for both.
- `destructive`/`read_only` added to the Unity<->Python wire protocol
  (`MCPToolDescriptor` in `Protocol/MCPMessage.cs`) so Python's `list_tools()` can see
  what each atomic tool's C# attribute actually declared.
- `server.py`'s `list_tools()` now builds a real `types.ToolAnnotations(destructiveHint=,
  readOnlyHint=, openWorldHint=)` for every tool. `openWorldHint` is `true` only for
  `manage_packages` (the one tool that hits the real Unity Package Manager registry over
  the network); every other tool only ever touches the local Unity project.
- `test_tool_annotations.py`: exercises the real `list_tools()` handler against the fake
  bridge and confirms the annotation JSON actually arrives on a real `types.Tool`, for
  one atomic read-only tool, one atomic tool with no hints set (confirming hints don't
  default to `true` by accident), the always-active `manage_tools` (not read-only, since
  it mutates active-group state), and one destructive composite.

## [1.23.0] — Tool scaling, phase 3: description-length audit + budget enforcement

Phase 3 of `docs/tool-scaling-strategy.md`. Text-only change to one tool description,
plus a new enforcement test -- no behavior change to any tool's function.

### Changed
- `wire_unity_event`'s description trimmed from 1,647 chars (~410 tokens) to ~720 --
  by far the worst offender in the whole 312-tool catalog, more than double the next
  largest. The engineering narrative (which live spikes found which gotchas) was cut;
  the operative behavior it was there to convey (null-event auto-init, EditorAndRuntime
  callState default, dynamic-vs-static listener selection) was kept, just stated
  directly instead of as a "confirmed via live spike" story. Every other atomic tool was
  already under 740 chars; no other change needed.

### Added
- `test_tool_description_budget.py`: parses every `[MCPTool(...)]` description directly
  from `Editor/Tools/*.cs` source and every composite tool's description from
  `workflows.all_workflows()`, asserting none exceed a 900-char hard cap (`manage_tools`
  exempted -- it intentionally carries phase 2's "groups at a glance" catalog). Catches
  a future oversized description the same audit-by-hand originally caught for
  `wire_unity_event`, without needing a human to re-run that audit by hand again.

## [1.22.0] — Tool scaling, phase 2: session-start workflow instructions + compact group catalog

Phase 2 (Tier 0 "cheap wins") of `docs/tool-scaling-strategy.md`. Pure Python, zero
per-turn cost -- both additions ride on data the client already receives once, not on
every tool call.

### Added
- `Server(..., instructions=...)` (`server.py`): a one-time, session-init-only message
  (confirmed via direct inspection of the installed `mcp==1.28.1` package that
  `instructions` flows into `InitializationOptions` and is sent exactly once at
  handshake, not repeated per turn) that tells a connecting AI client the intended
  workflow up front: call `manage_tools(action="search", query="...")` before guessing
  which group to activate, then `activate`/`deactivate` by group, keeping unused groups'
  full schemas out of context.
- A compact, single-line "Groups at a glance" catalog (`workflows._compact_group_catalog`)
  appended to `manage_tools`'s own tool description -- all 26 group names plus a
  truncated (`_truncate_group_description`, ~70 chars, trailing-stopword-aware) one-line
  description each, so a client can often pick the right group directly from
  `manage_tools`'s always-visible description without a separate `search` round trip for
  obvious cases, while `search` (phase 1) remains the tool for anything less obvious.

## [1.21.0] — Tool scaling, phase 1: BM25 tool search + batch group activation + soft budget guard

Phase 1 of `docs/tool-scaling-strategy.md` (the plan for making 312 tools feel
manageable to an AI client without hiding real capability). Pure Python -- no Unity C#
changes in this phase.

### Added
- `manage_tools(action="search", query, limit)`: BM25 keyword search (new
  `tool_search.py`, no new dependency -- a from-scratch, in-memory implementation, since
  the ~400-document corpus of atomic + composite tools + group descriptions is well
  within the range where plain BM25 gets results close to an embeddings model, at zero
  dependency/startup/network cost) across every tool's name, group, description, and
  parameter descriptions, plus each group's own description indexed as a
  group-level pseudo-document so broad queries (e.g. "scripted scare sequences") can
  surface the right *group* even when no individual tool name matches. Respects disabled
  groups exactly like every other discovery path (excluded entirely, not just
  deprioritized). Returns compact hits (tool/group name + one-line summary + whether
  the group is already active) rather than full schemas -- the token-expensive step
  (full schema exposure) still only happens once a group is actually activated.
- `manage_tools(action="activate"/"deactivate", groups=[...])`: batch form of the
  existing single-`group` parameter (kept, backward compatible) -- lets a search result
  spanning multiple groups be actioned in one round trip instead of one call per group.
- A soft activation-budget guard: activating a group now estimates the real, current
  description-token cost of the resulting active set and returns a `warning` field (the
  activation still proceeds) if it would exceed ~8,000 tokens -- guidance, not a block,
  so a legitimately large session isn't prevented from doing its job.

### Discovered / fixed during implementation
- A real BM25 quality gap, found and fixed before it shipped: without stemming, a
  natural-language query using a different word form than the tool's own name (e.g.
  "flickering" in the query vs. "flicker" in `add_flicker_light`) simply never matches --
  they're different tokens. Fixed with minimal, dependency-free suffix stripping
  (`tool_search._stem`) covering the common `-ing`/`-ed`/`-es`/plural-`s` cases; not a
  real Porter stemmer, just enough to close the gap that mattered here. Caught by
  `test_tool_search.py` before being trusted.

## [1.20.0] — Tool Groups window: human-only group disable + activate/deactivate control panel

### Added
- New `Window → Unity MCP → Tool Groups` (`MCPToolGroupsWindow.cs`): every tool group,
  collapsed by default, expandable to show its tools (atomic and composite) each with a
  one-line description. Shows Disabled/Active(default)/Inactive(default)/Always-active
  status per group.
- **Disable** (per group, `core` excluded): the group and its tools become entirely
  invisible to every AI client, not just hidden from listing -- a direct-by-name call to
  any of its tools now fails with the exact same "Unknown tool" message a genuinely
  nonexistent tool would get (see `MCPToolRegistry.Invoke`), and the group itself
  disappears from `manage_tools`' `list_groups` and can't be named via `activate`
  (treated as "Unknown group"). Deliberately indistinguishable from "doesn't exist" in
  every error path, so an AI client can never infer a disabled group's existence.
  Requires an explicit confirmation dialog; takes effect immediately, even for an AI
  session already mid-conversation.
- **Activate/Deactivate** (per group): sets the *default* active-group set for new AI
  sessions (seeds Python's `groups.py` `_active_groups` on process start and on
  `manage_tools reset`). Does not retroactively change an already-running session's own
  state -- a live session still controls that itself via `manage_tools`, the same as
  before this feature existed, right up until the disable override above.
- New `Library/MCP/tool_groups_config.json` (Unity writes, Python's `groups.py` reads,
  re-checked on a ~1s mtime-based throttle) and `Library/MCP/tool_manifest.json` (Python
  writes on server startup, Unity's new `MCPToolManifestReader.cs` reads) -- two small
  files instead of a protocol change, since the bridge's wire protocol only supports
  Python-initiates-request/Unity-responds, with no channel for Unity to push into or
  query an already-running Python process on demand.
- New `MCPToolGroupConfig.cs` (Unity-side read/write, refuses to disable `core`) and
  `MCPToolManifestReader.cs` (Unity-side read of Python's composite-tool/group-
  description manifest -- the only way Unity's window can know about composite tools at
  all, since they're hand-written Python in `workflows.py`, not `[MCPTool]`-attributed
  C# methods its own reflection scan can discover).

### Discovered / fixed
- A real bug in the mtime-based config re-read caching (`groups.py`): comparing a freshly
  read `mtime` (which is `None` both when a file has never existed AND right after it's
  been deleted) against a cached `None` short-circuited as "unchanged", silently keeping
  stale disabled/default-active state after the config file was deleted. Fixed by never
  treating a `None` mtime as equal to a cached value -- always reload in that case.
  Caught by the new `test_tool_group_disabling.py` before it could ship.
- `[MCPTool]`'s `group` is enforced at two independent points on the Unity side for
  defense in depth: `MCPCommandDispatcher.HandleListTools` (never advertises a disabled
  group's tools at all) and `MCPToolRegistry.Invoke` (refuses a direct call to one, same
  error as an unknown tool). Both checks read the same `MCPToolGroupConfig`, Unity's own
  in-memory copy of the config file it wrote, so enforcement doesn't depend on Python
  playing along.
- `server.py`'s `call_tool()` gained a matching disabled-group check for composite
  (Python `@workflow`) tools specifically -- Unity can't gate those itself since they
  never reach Unity's registry at all, so this is the only enforcement point for them.

## [1.19.0] — Tool catalog expansion, batch 18: Profiling + Build/Project/Packages (new `profiling`, `build` groups) — full 300-tool catalog complete

### Added
- `capture_profiler_frames`, `get_memory_snapshot`, `get_render_stats` (new `ProfilingTools.cs`, new `profiling`
  group), plus `analyze_performance` (`workflows.py` composite over `get_scene_stats`/`get_render_stats`/
  `get_memory_snapshot` flagging too many realtime lights/colliders/draw calls/SetPass calls).
- `build_player`, `configure_build_settings`, `manage_packages`, `manage_project_settings` (new `BuildTools.cs`,
  new `build` group). This is the last batch in the `unity-mcp-300-tools-fps-horror.md` catalog -- all 26 groups
  now have at least one tool.
- New `MCPPathGuard.TryResolveWithinProject`, alongside the existing `TryResolveWithinAssets` -- confines a path to
  the project root instead of `Assets/`, for `build_player`'s output path, which legitimately needs to write
  outside `Assets/` but must not be allowed to escape the project root entirely.

### Discovered -- all confirmed via live spikes against a real Unity Editor
- `UnityEditorInternal.ProfilerDriver`, `UnityEditor.Profiling.HierarchyFrameDataView`, and `UnityEditor.UnityStats`
  are all real, directly-compilable public APIs despite their "Internal"-sounding namespaces -- no reflection
  needed, unlike most of this codebase's optional-package integrations (Timeline/Cinemachine/Input System/etc).
- `ProfilerDriver`'s frame buffer is empty (`lastFrameIndex == -1`) and `UnityStats`' render counters read all
  zero until a real Play Mode/Development Player session has actually run/rendered frames -- both `capture_
  profiler_frames` and `get_render_stats` now report that gracefully (empty frames / real zero values with an
  explanatory note) rather than treating it as an error.
- `UnityEditor.PackageManager.Client.List/Add/Remove/Search` all return async `*Request` objects whose completion
  is driven independently of the calling thread -- confirmed via live spike that a bounded `Thread.Sleep`-based
  spin-wait resolves correctly (List/Search in well under a second; a full package add+remove round trip against
  the real Unity registry in the verify project) without deadlocking the Editor's main thread, unlike the
  domain-reload-driven tools elsewhere in this codebase that require the agent to poll via a separate LOOP tool.
- Tag/layer editing has no public API beyond the well-known `SerializedObject`-over-
  `ProjectSettings/TagManager.asset` technique -- confirmed via spike that edits actually persist and are visible
  via `InternalEditorUtility.tags`/`layers` afterward. Layer indices 0-7 are Unity's reserved built-in layers;
  `manage_project_settings` refuses to touch them.
- A real end-to-end `build_player` test in the verify project genuinely failed (a pre-existing ShaderGraph
  assembly-resolution issue in that scratch project, unrelated to this tool) -- useful confirmation that
  `BuildPipeline.BuildPlayer()` failures come back as real, structured `BuildReport` data (`result`, `totalErrors`,
  `totalWarnings`, `totalTime`, `totalSize`, `outputPath`) rather than an uncaught exception, which is exactly the
  contract `build_player` needed to expose either way.

### Scope decisions
- `manage_packages`' `remove` action requires `confirm: true`, checked manually inside the method body rather than
  via the framework's automatic destructive-gate attribute, since a single multi-action tool (list/add/remove/
  search) can't be conditionally destructive per-action at the `[MCPTool]` attribute level.
- `configure_build_settings` doesn't duplicate scenes-in-build (already `add_scene_to_build`/`list_scenes_in_build`,
  scene group) or icons (no stable, version-independent public API surface across build targets worth the
  complexity here). `manage_project_settings` doesn't duplicate physics settings (`configure_physics_settings`,
  physics group) or graphics tiers (no stable public API).

## [1.18.0] — Tool catalog expansion, batch 17: Terrain + Timeline + Level Gen + Input (new `terrain`, `timeline`, `levelgen`, `input` groups)

### Added
- `create_terrain`, `sculpt_terrain_height`, `add_terrain_layer`, `paint_terrain_texture`, `place_terrain_trees`,
  `place_terrain_details`, `paint_terrain_holes`, `create_wind_zone` (new `TerrainTools.cs`, new `terrain` group),
  plus `scatter_props` (`workflows.py` composite over `instantiate_prefab` + `snap_to_ground`).
- `create_timeline`, `add_timeline_track`, `add_timeline_clip`, `add_timeline_signal`, `bind_timeline_track`,
  `play_timeline`, `add_camera_cut_track` (new `TimelineTools.cs`, new `timeline` group), plus
  `create_scare_sequence` (`workflows.py` composite choreographing a light-flicker Activation track, an optional
  Animation track, and Signal-track beats for an audio stinger and a camera shake).
- `configure_lod_group`, `generate_lightmap_uvs`, `bake_occlusion_culling` (new `LevelGenTools.cs`, new `levelgen`
  group), plus `generate_grid_layout`, `place_spawn_points`, `carve_room`, `connect_rooms`, `set_scene_streaming`,
  `validate_level_navmesh` (`workflows.py` composites -- all six are pure Python compositions over existing
  atomic tools, no new reflection needed).
- `create_input_action_asset`, `list_input_action_maps`, `add_input_action`, `add_input_binding`,
  `set_action_map_active`, `simulate_input` (new `InputTools.cs`, new `input` group), plus
  `generate_input_reader` and `add_rebinding_ui` (`workflows.py` composites). Scaffolds `MCPSceneStreamer`,
  `MCPRebindButton`, and per-call InputReader ScriptableObject classes.

### Fixed / discovered -- all confirmed via live spikes against a real Unity Editor
- **Terrain**: a freshly-created `TerrainData`'s `detailResolution` is `0`; any `GetDetailLayer`/`SetDetailLayer`
  call throws `IndexOutOfRangeException` until `SetDetailResolution()` is called first -- `create_terrain` now
  does this up front. Heightmap/alphamap/detail arrays are indexed `[z, x]`, confirmed by raising terrain at a
  known world position and checking the array's middle index.
- **Timeline**: Signal tracks are Marker-based (`SignalTrack : MarkerTrack`) -- emitters need
  `TrackAsset.CreateMarker<SignalEmitter>(time)`, not `CreateClip<T>()` (neither `SignalAsset` nor `SignalEmitter`
  implement `IPlayableAsset`, so `CreateClip<T>()` against them fails to compile). `CinemachineTrack`/
  `CinemachineShot` have no namespace prefix in the Cinemachine assembly, unlike most Cinemachine types.
  `PlayableDirector`/`Playable` are core Unity, referenced directly; everything else in `com.unity.timeline` is
  reflected. `Evaluate()` after setting `.time` really re-samples outside Play Mode (same category as
  `Animator.Play()` + `Update(0)`).
- **Input System**: `InputActionAsset.ToJson()` throws `ArgumentNullException` against a genuinely blank
  `ScriptableObject.CreateInstance<InputActionAsset>()` (its internal `m_ActionMaps` field is `null`, not an
  empty array, until the asset round-trips through the real importer once) -- `create_input_action_asset` now
  writes a minimal hand-built JSON template directly for creation only. `InputActionAsset`/`InputActionMap` have
  **no instance** `AddActionMap`/`AddAction` methods at all -- they're static extension methods on
  `InputActionSetupExtensions`, found only after an instance-method lookup returned `null` and threw on
  `Invoke()`. `InputBinding.path`/`.groups` are properties, not fields (an early version of `list_input_action_maps`
  used `GetField` and got a silent `NullReferenceException`). `InputSystem.AddDevice<TDevice>()`'s generic
  overload takes one `string name` parameter, not zero. `InputDevice`/`InputControl` are plain C# objects, not
  `UnityEngine.Object` subclasses -- casting one throws `InvalidCastException`.
- **`simulate_input`'s real scope**: originally implemented with `InputSystem.QueueDeltaStateEvent`, which throws
  `InvalidOperationException` ("Cannot send delta state events against bitfield controls") for any digital/button
  control -- confirmed this isn't fixable by switching to `InputState.Change()` either, since Unity's own
  higher-level API rejects bitfield controls with the same kind of `ArgumentException` for the same underlying
  reason. Kept `InputState.Change()` (cleaner than the delta approach, works correctly for analog/axis controls,
  verified via spike against a real `IntegerControl`), and `simulate_input` now surfaces the bitfield rejection as
  a clear, actionable failure instead of crashing or silently no-op'ing -- the same honest-scope-decision pattern
  as `create_audio_mixer`'s group-creation limitation (batch 13).

### Scope decisions
- `generate_input_reader` scaffolds a hand-written SO reader hooking `InputActionMap.FindAction` directly at
  runtime, rather than driving Unity's C# code-generation importer feature (`m_GenerateWrapperCode` and friends) --
  avoids a second, more fragile reflection surface for no real capability gain, and matches this codebase's
  existing preference for hand-written scaffolded scripts over Unity code-generation elsewhere.
- `add_rebinding_ui`'s interactive rebind (`InputActionRebindingExtensions.PerformInteractiveRebinding`) and
  `simulate_input` against digital controls both need a real input device or a real Play Mode session to verify
  end-to-end -- marked Manual Test rather than forced through a headless workaround.

## [1.17.0] — Tool catalog expansion, batch 16: Gameplay Systems & Data (new `gameplay` group)

### Added
- `set_scriptable_object_values`, `wire_event_listener`, `save_game_state`, `load_game_state` (new
  `GameplayTools.cs`, new `gameplay` group).
- `define_scriptable_object_type`, `create_event_channel`, `create_save_system`, `create_game_manager`,
  `create_inventory_system`, `create_interactable`, `create_door`, `create_key_lock_pair`, `create_checkpoint`,
  `create_objective_system` (`workflows.py` composites, `gameplay` group). Scaffolds `MCPSaveData`,
  `MCPSaveSystem`, `MCPGameManager`, `MCPInventory`, `MCPInteractable`, `MCPDoor`, `MCPKeyItem`, `MCPCheckpoint`,
  `MCPObjectiveSystem`, `MCPObjectiveListUI`.
- New internal `MCPUnityEventWiring` helper (`Editor/Tools/MCPUnityEventWiring.cs`), factoring the
  dynamic/static/void-fallback persistent-listener logic shared by `wire_unity_event` and the new
  `wire_event_listener` -- the first time this codebase has shared bridge-internal logic across two tool files
  rather than duplicating it (unlike the deliberate scaffolded-user-script duplication elsewhere, there's no
  cross-project-compile-coupling risk here).

### Fixed -- a real bug in batch 15's `wire_unity_event`, found while building `create_key_lock_pair`
- `wire_unity_event`/`wire_event_listener` now default to a **dynamic** listener that forwards the event's real
  runtime argument. Previously (batch 15) they only ever used `UnityEventTools.AddStringPersistentListener`/etc,
  which bakes a **fixed constant** into the persistent call and ignores whatever the event actually raises --
  confirmed via live spike that forwarding the real value requires `UnityEventTools`' plain generic
  `AddPersistentListener<T>` overload instead, a genuinely different method, not just a parameter toggle. This
  means `create_interaction_prompt` (batch 15) was silently showing a blank baked string instead of the real
  detected interactable's prompt every time -- retroactively fixed by this change alone, with no change needed
  to that composite, since it never passed a `stringArgument` and the new default now does the right thing.
  `dynamic: false` plus the `*Argument` params reproduces the old baked behavior for callers that genuinely want
  a fixed constant.
- `wire_unity_event`/`wire_event_listener` also now fall back to a "static" listener (ignoring the event's
  runtime argument, calling a parameterless method) when `methodName` has no overload matching the event's own
  generic argument at all -- needed for `create_checkpoint`'s `MCPTriggerRelay.onTriggerEnter` (`UnityEvent<Collider>`)
  wired to a plain `Activate()`. Confirmed via live spike this is a real, first-class `UnityEventTools` capability
  (the same Static/Dynamic argument mode the Inspector's own UI exposes), not a workaround.

### Scope decisions
- `create_scriptable_object` (already in `assets`) and `create_health_system` (already covered by `weapons`'
  `create_damage_receiver`) are treated as already covered -- the same dedup `enemy_ai`'s batch applied to
  `scaffold_behavior_tree`/`add_bt_node`/`connect_bt_nodes`.
- `MCPSaveSystem`/`MCPInventory`/`MCPObjectiveSystem` all use the same "the C# script only ever holds an opaque
  JSON string field, all parsing/merging happens in Python" pattern `MCPBlackboard` established -- except
  `MCPSaveSystem`, which genuinely needs to *aggregate* many objects' JSON blobs into one file at runtime, so it
  uses `JsonUtility` with `[System.Serializable]` wrapper classes shaped exactly like the save format (no
  Newtonsoft dependency required in the target project).
- Every "attach once" scaffolded script in this batch is marked `[DisallowMultipleComponent]`, confirmed via live
  spike that `Undo.AddComponent` then returns `null` (not an exception, not a duplicate) against an
  already-present type of that kind -- makes composites like `create_key_lock_pair` (which unconditionally
  ensures `MCPDoor` is present on a door that might already have one) safe to call idempotently.

### Verified
- All 4 new atomic tools were invoked end-to-end against a real Unity Editor: real ScriptableObject field writes
  (float/string/bool), a real SO event channel with a real persistent listener in both static-baked and
  dynamic-forwarding modes, a real `UnityEvent<Collider>` wired to a parameterless method via the new
  static-listener fallback, and a real save/load JSON file round trip through `Application.persistentDataPath`
  (including a rejected path-traversal slot name).
- All 10 new composites' scaffolded scripts were verified for real behavior: `MCPGameManager.SetState()` really
  updates state and fires a real event; `MCPDoor` really blocks `Interact()` while locked and only unlocks with
  the matching key; `MCPKeyItem.Interact()` really fires `onPickup` with the real keyId (self-destruction is
  real code, verifiable only in Play Mode -- `Object.Destroy()` is a documented no-op in Edit Mode); `MCPCheckpoint`
  really records its position and `Respawn()`s a target there; `MCPSaveSystem.SaveSlot`/`LoadSlot` really
  round-trips `MCPSaveData.data` through a real file, surviving an in-between mutation. All 10 composites were
  additionally verified via `FakeBridge`-based Python tests confirming exact call sequences and defaults.

## [1.16.0] — Tool catalog expansion, batch 15: UI/HUD extensions + Animation (extend `ui`, new `animation` group)

### Added
- `create_animator_controller`, `add_animator_state`, `add_animator_transition`, `add_animator_parameter`,
  `create_blend_tree`, `assign_animator`, `play_animation`, `list_animation_clips`, `add_animation_event`,
  `configure_avatar_mask`, `set_root_motion`, `add_ik_constraint` (new `AnimationTools.cs`, new `animation` group).
- `wire_unity_event` (`ComponentTools.cs`, `core` group) -- adds a persistent `UnityEvent`/`UnityEvent<T>` listener
  by path/type/method name via `UnityEditor.Events.UnityEventTools`, the programmatic equivalent of the
  Inspector's own '+' button.
- `create_health_bar`, `create_ammo_counter`, `create_crosshair`, `create_interaction_prompt`,
  `create_pause_menu`, `create_subtitle_system` (`workflows.py` composites, `ui` group). Scaffolds
  `MCPValueBarUI`, `MCPAmmoCounterUI`, `MCPCrosshairUI`, `MCPInteractionPromptUI`, `MCPPauseMenuUI`,
  `MCPSubtitleUI`.
- Added `com.unity.animation.rigging` to the verification project (resolved from the network registry --
  confirmed this sandbox has real internet access via successful package resolution, unlike URP/VFX Graph which
  ship bundled with the Editor install) so `add_ik_constraint`'s API was confirmed against the real package
  rather than guessed.

### Scope decisions
- `add_ui_text`/`add_ui_image`/`add_ui_button`/`add_layout_group` from the source catalog are treated as already
  covered by the existing `create_ui_element` (Panel/Button/Text/Image/InputField in one tool) and `set_layout`
  -- the same dedup reasoning `enemy_ai`'s batch applied to `scaffold_behavior_tree`/`add_bt_node`/
  `connect_bt_nodes`.
- `create_health_bar`/`create_ammo_counter` are NOT wired to `MCPHealth`/ammo scripts directly -- doing so would
  create a hard dependency on the `weapons` group. Instead they expose `SetValue`/`SetAmmo` methods for gameplay
  code to call.
- `create_interaction_prompt` and `create_pause_menu` ARE really wired end-to-end via the new `wire_unity_event`
  tool (real `MCPInteractionRaycaster` UnityEvents, real `Button.onClick`), since a "prompt bound to the
  interaction ray" that isn't actually bound would defeat the composite's purpose -- this is what motivated
  building `wire_unity_event` this batch instead of deferring it.

### Fixed (design-time, caught before compiling by live spike, not by a failed build)
- `AnimatorController.parameters` (and same-shaped array properties) return a fresh deserialized copy on every
  read -- reassigning `x.parameters = x.parameters` after mutating a separately-fetched element is a silent
  no-op. Fixed by mutating the one fetched array's element and writing that exact array back.
- `TwoBoneIKConstraint`/`MultiAimConstraint`'s configurable data lives behind a protected `m_Data` field on the
  generic `RigConstraint<,,>` base class; the public `data` property is a ref-return that throws
  `NotSupportedException` via reflection `Invoke`. `WeightedTransform.transform` is a plain field, not a
  property -- the same field-vs-property trap `Volume.priority`/`weight`/`blendDistance` set off in batch 14.
- A `UnityEvent`/`UnityEvent<T>` field on a component added via `AddComponent()` this session is `null`, not an
  empty event -- unlike a component added through the Inspector's "Add Component" UI. `wire_unity_event`
  auto-instantiates it before adding a listener.
- `UnityEventTools.AddPersistentListener` defaults the new listener's call state to `RuntimeOnly`, which silently
  never fires outside Play Mode -- confirmed by a listener that registered successfully
  (`GetPersistentEventCount() == 1`) but never actually invoked until the call state was set to
  `EditorAndRuntime`. `wire_unity_event` defaults to `EditorAndRuntime` instead (a strict superset -- it still
  fires normally in Play Mode).

### Verified
- All 12 new animation tools plus `wire_unity_event` were invoked end-to-end against a real Unity Editor: real
  states/transitions/conditions/parameters (including the array-copy gotcha), a real 2-child BlendTree, a real
  Animator actually transitioning state via `Play()`/`Update(0)` outside Play Mode, a real `AnimationEvent`
  round-tripped through a saved clip, a real `AvatarMask` with real body-part toggles, real
  `TwoBoneIKConstraint`/`MultiAimConstraint` components with real wired Transforms (confirming `add_ik_constraint`
  reuses one Rig/RigLayer across multiple calls rather than duplicating it), and a real `UnityEvent<string>`
  persistent listener that actually fired outside Play Mode.
- All 6 new UI composites were verified via `FakeBridge`-based Python tests confirming exact call sequences,
  including the two real `wire_unity_event` calls each in `create_interaction_prompt`/`create_pause_menu`.

## [1.15.0] — Tool catalog expansion, batch 14: Rendering/Post-FX + VFX (new `rendering` + `vfx` groups)

### Added
- `get_render_pipeline`, `create_post_process_volume`, `set_volume_profile`, `add_vignette`, `add_bloom`,
  `add_depth_of_field`, `add_chromatic_aberration`, `add_motion_blur`, `add_lens_distortion`, `add_film_grain`,
  `add_color_grading`, `set_camera_clear_and_fog`, `toggle_ssao` (new `RenderingTools.cs`, new `rendering` group).
- `create_particle_system`, `set_particle_module`, `play_particle_system`, `create_vfx_graph`, `add_decal`,
  `create_fog_volume`, `create_trail` (new `VfxTools.cs`, new `vfx` group).
- `add_dust_motes`, `add_blood_splatter`, `add_breath_fog` (`workflows.py` composites, `vfx` group). Scaffolds
  `MCPBreathFog`.
- New `rendering` and `vfx` groups registered in `groups.py`.
- Added `com.unity.render-pipelines.universal` and `com.unity.visualeffectgraph` to the verification project
  (both ship bundled with the Editor install, no internet required) so every URP/VFX Graph API used here was
  confirmed against the real packages rather than guessed from documentation -- the same standard applied to
  every other batch, just with the packages actually installed this time instead of reflected against blind.

### Scope decisions
- Every `rendering`/`add_decal`/`create_vfx_graph` tool is scoped to **URP only** and resolves its types via
  reflection (`Type.GetType(..., "Unity.RenderPipelines.Universal.Runtime")`), the same pattern as Cinemachine/
  Shader Graph/AudioMixer -- HDRP has its own, differently-shaped effect overrides not supported here, and the
  bridge must still compile in Built-in-only projects.
- `create_fog_volume` uses a real soft-particle cloud instead of a native volumetric fog volume: this URP version
  (17.0.4) has no "Local Volumetric Fog" type at all (confirmed by searching the installed package's own source,
  not just failing to find it via reflection) -- that's an HDRP/newer-URP-only feature.
- `add_blood_splatter` approximates a one-shot particle burst via a brief, high emission rate on a non-looping
  system rather than adding `ParticleSystem.SetBursts()`-style fields to `set_particle_module` for one composite's
  sake.
- `toggle_ssao` only enables/disables an existing-or-newly-added SSAO renderer feature; it doesn't remove one,
  matching the real inspector checkbox's behavior rather than inventing a more destructive "remove" action nobody
  asked for.

### Fixed (design-time, caught before compiling by reading URP's own source rather than guessing)
- `Volume.priority`/`weight`/`blendDistance` are plain public **fields**, while `isGlobal`/`profile` on the very
  same component are **properties** -- confirmed by reading `Volume.cs`'s actual source after a live invoke test
  threw `NullReferenceException` from blindly calling `GetProperty` on all five.

### Verified
- All 20 new atomic tools were invoked end-to-end against a real Unity Editor with URP and VFX Graph installed:
  a real `Volume`/`VolumeProfile` asset with every override's fields (Vignette, Bloom, DepthOfField,
  ChromaticAberration, MotionBlur, LensDistortion, FilmGrain, ColorAdjustments, WhiteBalance, Tonemapping) read
  back correctly after a full save-and-reload from disk; a real Camera synced to `RenderSettings.fogColor`; a
  real SSAO renderer feature added via the exact `SerializedObject`/`m_RendererFeatures` mechanism URP's own
  inspector uses (read from `ScriptableRendererDataEditor.cs`), confirmed to not duplicate on a second call and to
  really toggle `isActive`; a real `ParticleSystem` with main/shape/emission/colorOverLifetime/noise all applied
  and read back; a real loadable `VisualEffectAsset`; a real `DecalProjector` with size/fadeFactor applied; and a
  real `TrailRenderer` (its start/end color read back with expected `Color32`-backed quantization, not a bug).
- All 3 new composites were verified via `FakeBridge`-based Python tests confirming exact call sequences and
  defaults.

## [1.14.0] — Tool catalog expansion, batch 13: Audio (new `audio` group)

### Added
- `add_audio_source`, `set_audio_source_properties`, `configure_spatial_audio`, `add_audio_listener`,
  `create_audio_mixer`, `set_mixer_parameter`, `add_reverb_zone`, `play_sound` (new `AudioTools.cs`, new `audio`
  group). Real atomic wraps of `AudioSource`/`AudioListener`/`AudioReverbZone`/`AudioMixer`.
- `add_audio_occlusion`, `add_ambient_bed`, `add_scare_stinger`, `add_footstep_audio_set`, `add_dynamic_music`
  (`workflows.py` composites, `audio` group). Scaffolds `MCPAudioOcclusion`, `MCPAmbientFade`, `MCPScareStinger`,
  `MCPSurfaceClip` + `MCPSurfaceFootsteps`, and `MCPDynamicMusic`.
- New `audio` group registered in `groups.py`.

### Scope decisions
- `create_audio_mixer` drives `UnityEditor.Audio.AudioMixerController` entirely via reflection: it's an internal
  class in a core Unity assembly (confirmed via `CS0122: inaccessible due to its protection level` on a direct
  reference), and `AudioMixer` itself can't be created with `ScriptableObject.CreateInstance` (confirmed via
  `CS0311`). Live spikes against a real Editor found `CreateNewGroup`/`AddGroupToCurrentView` don't reliably
  attach a new group into the mixer's persisted group tree without further internal Editor view-state setup
  (`AddGroupToCurrentView` threw `IndexOutOfRangeException` on a fresh controller with no existing view list).
  Rather than keep reverse-engineering increasingly fragile internal state, `create_audio_mixer` is scoped to
  creating the mixer asset with its default Master group only -- the same kind of deliberate, documented scope
  reduction `create_shader_graph`/`mark_addressable`/`configure_navmesh_settings` made for their own real
  API limitations.
- `set_mixer_parameter` can only set a parameter that's already been exposed to scripting via the Mixer window's
  own UI ("Expose ... to script") -- there's no reflection path exercised here to expose new parameters
  programmatically, so this is a hard, documented limitation rather than an oversight.
- `add_footstep_audio_set` ships a fully self-contained `MCPSurfaceFootsteps` instead of extending
  `fps_controller`'s `MCPFootsteps` -- referencing it directly would create the same kind of cross-batch
  compile-time coupling `MCPEnemyBrain` avoided via `SendMessage` in batch 12, except there's no method call to
  redirect through here, so self-containment (accepting some duplication) is the fix instead.

### Verified
- All 8 atomic tools were invoked end-to-end against a real Unity Editor: a real `AudioSource`'s
  spatialBlend/min/maxDistance/clip/volume/pitch/loop/priority/playOnAwake/mute/rolloff/spread/dopplerLevel all
  round-tripped correctly (including loading a real imported `.wav` `AudioClip`); `add_audio_listener` correctly
  reported `alreadyPresent` and every other listener's path across a 2-listener scene; `add_reverb_zone` applied
  real fields to a real `AudioReverbZone`; `play_sound` returned a real, non-zero `clipLength`; and -- the
  highest-risk check given how much spike effort went into confirming this path -- `create_audio_mixer` produced
  a real, loadable `.mixer` asset with a findable `Master` group, and `set_mixer_parameter` was confirmed to fail
  with the documented "not exposed to scripting" message against that same real, freshly-created mixer.
- All 6 new scaffolded scripts' exact string content was written into the verification project and confirmed to
  compile cleanly alongside `AudioTools.cs` (163 atomic tools total, zero `error CS` lines).
- `MCPAudioOcclusion`/`MCPSurfaceFootsteps`/`MCPDynamicMusic`/`MCPAmbientFade`/`MCPScareStinger`'s `Update()`-loop
  behavior depends on `Awake()` having populated fields (`_surfaceClips`, `_layers`, `_audioSource`), which isn't
  guaranteed immediately after `AddComponent()` outside Play mode (per the batch 11 finding) -- structure, fields,
  and wiring were verified for real instead, with the `Awake()`-dependent runtime behavior marked **Manual Test**.

## [1.13.0] — Tool catalog expansion, batch 12: Enemy AI (extend `behavior_tree` + new `enemy_ai` group)

### Added
- `set_blackboard_key` (`workflows.py` composite, `behavior_tree` group) — scaffolds `MCPBlackboard` (a single
  JSON string field) and round-trips a key through it via `get_component_field`/`set_component_field`.
- `create_enemy`, `add_sight_sensor`, `add_hearing_sensor`, `add_patrol_route`, `configure_chase_behavior`,
  `configure_search_behavior`, `configure_attack_behavior`, `add_stalker_ai`, `add_enemy_spawner` (`workflows.py`
  composites, new `enemy_ai` group). Scaffolds `MCPEnemyBrain` (a consolidated patrol/chase/search/attack/stalk
  state machine over one `NavMeshAgent`, same "tightly-coupled state lives in one component" reasoning as
  `MCPFPSController` in batch 10), `MCPSightSensor`, `MCPHearingSensor` + the static `MCPNoiseEvents` bus, and
  `MCPEnemySpawner`.
- New `enemy_ai` group registered in `groups.py`.

### Scope decisions
- The source catalog lists `scaffold_behavior_tree`/`add_bt_node`/`connect_bt_nodes` as new tools for this batch,
  but their purpose (build/extend a tree from nodes with parent/child relationships) is already covered by the
  existing `behavior_tree` group's `scaffold_behavior_tree_framework`/`create_behavior_tree`/
  `add_behavior_tree_node` composites. Rather than ship a second, overlapping Behavior Tree mechanism, those three
  are treated as already covered -- the same kind of dedup Part A of this project did for genuinely duplicate
  tools.
- `set_blackboard_key` is listed as atomic in the source catalog but ships as a Python composite: a real Blackboard
  has to be a scaffolded user-project script (the bridge's compiled C# can't reference a type that only exists in
  the target project's Assembly-CSharp), the same reasoning that put every fps_controller/weapons tool in Python
  instead of new `[MCPTool]` methods.
- `MCPEnemyBrain.PerformAttack()` calls `TryAttack()`/`TryFire()` via `gameObject.SendMessage(...,
  SendMessageOptions.DontRequireReceiver)` rather than `GetComponent<MCPMeleeAttack>()`/etc. directly -- a direct
  type reference would make `MCPEnemyBrain.cs` fail to compile in any project that never scaffolded the `weapons`
  group's scripts, since those three types wouldn't exist yet. This was caught by reasoning through the dependency
  graph before writing the script, not by a failed compile.

### Verified
- All 5 new scaffolded scripts' exact string content was written into the verification project and confirmed to
  compile cleanly (including the `SendMessage`-based decoupling from the weapons group's optional scripts).
- Every composite's field names and object-reference wiring were verified against real components. `MCPEnemyBrain`
  was confirmed to accept every configure_*/stalker field, and `SetState()` was confirmed to genuinely transition
  `currentState` (the one piece of brain behavior that doesn't depend on `Awake()` having already run).
  `set_blackboard_key`'s exact mechanism -- reading `MCPBlackboard.data` via `get_component_field`, merging a key
  into the parsed JSON, writing it back via `set_component_field` -- was verified end-to-end against a real
  component.
- `MCPEnemyBrain`'s Update()-loop movement, `MCPSightSensor`/`MCPHearingSensor`'s detection, and
  `patrolRouteParent`'s child-waypoint auto-discovery all depend on `Awake()` having populated private fields,
  which (per the batch 11 finding) isn't guaranteed immediately after `AddComponent()` outside Play mode --
  structure/fields/wiring were verified for real instead, with the `Awake()`-dependent runtime behavior marked
  **Manual Test**.

## [1.12.0] — Tool catalog expansion, batch 11: Weapons & Combat (new `weapons` group)

### Added
- `create_weapon`, `configure_hitscan`, `configure_projectile`, `add_ammo_system`, `add_recoil`, `add_muzzle_flash`,
  `add_weapon_sway`, `add_hit_reaction`, `add_melee_attack`, `create_damage_receiver`, `add_weapon_switching`
  (`workflows.py` composites, new `weapons` group). No new atomic C# tool this batch -- every tool is built by
  orchestrating existing generic tools against newly scaffolded scripts: `IDamageable` + `MCPHitReaction` (shared
  foundation, scaffolded defensively by whichever composite needs them first), `MCPHitscanWeapon`, `MCPProjectile`
  + `MCPProjectileWeapon`, `MCPAmmoSystem`, `MCPRecoil`, `MCPMuzzleFlash`, `MCPWeaponSway`, `MCPMeleeAttack`,
  `MCPHealth` + `MCPHitZone`, `MCPWeaponSwitcher`.
- New `weapons` group registered in `groups.py`.

### Design notes
- `wire_object_reference`/`batch_wire_references` only support wiring a single object reference per field, not
  array elements -- there's no tool-level way to wire a `GameObject[]` array. `MCPWeaponSwitcher` is designed
  around this: instead of a wired weapons array, it auto-discovers its own direct children as weapon slots at
  `Awake()`, and `add_weapon_switching` reparents any given `weaponPaths` under the holder first.
- `configure_projectile` auto-creates a minimal default projectile prefab (a small trigger-collider sphere with
  `MCPProjectile`) when no `projectilePrefabPath` is given, via a real `create_primitive` → `add_collider` →
  `add_component` → `create_prefab` → `delete_gameobject` sequence -- confirmed to produce a genuinely loadable,
  correctly-configured prefab asset.

### Verified
- All 13 new scaffolded scripts' exact string content (pulled directly from `workflows.py`) was written into the
  verification project and confirmed to compile cleanly.
- Every composite's field names and object-reference wiring were verified against real components via the same
  generic tools the composites call.
- `MCPHitscanWeapon.TryFire()` and `MCPMeleeAttack.TryAttack()` were exercised end-to-end against a real
  `MCPHealth`-bearing target: real `Physics.Raycast`/`Physics.SphereCast` hits, real damage applied through
  `IDamageable`, and `MCPHitZone`'s damage multiplier confirmed to route correctly into the wired `MCPHealth`
  (100 → 75 → 60 → 30 across hitscan → melee → headshot-zone damage, in one continuous test). Getting this test
  to pass surfaced a real Edit-mode gotcha (not a product bug): `Physics.Raycast`/`SphereCast` don't reliably see
  colliders created or moved earlier in the same frame without an explicit `Physics.SyncTransforms()` call first --
  Play mode's continuous simulation doesn't have this problem, but a batchmode Editor script firing a raycast
  immediately after building the scene does.
- `MCPMuzzleFlash.Flash()` and `MCPWeaponSwitcher`'s child-discovery both depend on `Awake()` having already run,
  which isn't guaranteed immediately after `AddComponent()` outside Play mode (confirmed via a live
  `NullReferenceException` on a direct attempt) -- their fields/wiring/structure were verified for real instead,
  with the `Awake()`-dependent runtime behavior itself marked **Manual Test**.

## [1.11.0] — Tool catalog expansion, batch 10: FPS Character Controller (new `fps_controller` group)

### Added
- `add_character_controller` (new `fps_controller` group, new `CharacterControllerTools.cs`) -- the batch's only
  atomic tool.
- `create_fps_player`, `configure_ground_movement`, `configure_sprint`, `configure_crouch`, `configure_jump`,
  `add_head_look`, `add_footstep_system`, `add_interaction_raycaster`, `add_stamina_system`, `add_flashlight`,
  `add_lean_system` (`workflows.py` composites, `fps_controller` group). Unity has no built-in FPS controller, so
  these scaffold 7 new hand-written scripts the same idempotent way `MCPTriggerRelay` is scaffolded:
  `MCPFPSController` (ground movement + sprint + crouch + jump share one `CharacterController.Move()` call per
  frame, so they live in one component rather than four independently-added ones), `MCPMouseLook`, `MCPFootsteps`,
  `IInteractable` + `MCPInteractionRaycaster`, `MCPStamina`, `MCPFlashlight`, `MCPLean`. Movement/look inputs
  (`moveInput`, `lookInput`, `jumpRequested`) are public fields meant to be driven by an input system (batch 17)
  rather than read directly from Unity's old Input Manager or the newer Input System package -- that's a
  per-project choice this MCP server shouldn't force.
- New `fps_controller` group registered in `groups.py`.

### Fixed
- `Undo.AddComponent<T>()` left `NavMeshAgent`/`NavMeshObstacle` as stale references in batch 9 (see 1.10.0) --
  `add_character_controller` uses plain `AddComponent<T>()` from the start, consistent with that finding.

### Verified
- All 7 scaffolded scripts' exact string content (pulled directly from `workflows.py`, not retyped) was written
  into the verification project and confirmed to compile cleanly in a real Unity Editor -- this closes a real gap
  in every prior batch's composite-script testing, which only exercised the Python-side orchestration logic
  against a `FakeBridge` and never actually compiled the scaffolded C# for real.
- `add_character_controller` was exercised against a real Unity Editor instance: real radius/height/center/
  slopeLimit applied and read back, plus confirmed idempotent (a second call reuses the existing component rather
  than duplicating it).
- Every composite's exact field names and object-reference wiring were verified against real components via the
  same generic tools the composites themselves call (`add_component`, `set_component_properties_batch`,
  `wire_object_reference`) -- this caught zero bugs in the scripts themselves, but did catch and fix a path bug
  in the test's own setup (a nested camera GameObject's hierarchy path). Actual movement *feel* (jump arc,
  coyote-time window, mouse-look responsiveness) needs a real Play mode session with real input and is marked
  **Manual Test** in the tool catalog -- verifying it headlessly would need a full Play-mode automation pass for
  comparatively little additional confidence over the compile + wiring checks already done.

## [1.10.0] — Tool catalog expansion, batch 9: NavMesh & Navigation (new `navmesh` group)

### Added
- `bake_navmesh`, `configure_navmesh_settings`, `add_navmesh_agent`,
  `set_agent_destination`, `add_navmesh_obstacle`, `create_offmesh_link`,
  `define_navmesh_area`, `mark_navmesh_area`, `sample_navmesh`,
  `bake_navmesh_volume` (new `navmesh` group, new `NavMeshTools.cs`). All 10
  are atomic C# tools -- no new Python composites this batch.
- New `navmesh` group registered in `groups.py`.

### Fixed / discovered (all via live spikes against a real Unity Editor before writing the tools)
- There is no public scripting API to modify an EXISTING agent type's build
  settings (radius/height/slope/step) -- `NavMesh.CreateSettings()` only
  returns a new struct with fixed default values, and mutating that struct's
  fields is a confirmed silent no-op: reading the settings back by ID
  afterward (`NavMesh.GetSettingsByID`) still shows the original values. Also
  confirmed a red herring: `UnityEditor.AI.NavMeshBuilder.navMeshSettingsObject`
  looks like a plausible backing store (it's a real, SerializedObject-editable
  asset) but editing it has no effect on `GetSettingsByID` either -- it's an
  unrelated, disconnected object. Given this, `configure_navmesh_settings`
  stores its values as this MCP server's own session defaults for
  `bake_navmesh_volume`, rather than claiming to edit Unity's real (and
  actually non-scriptable) agent type registry.
- `UnityEditor.AI.NavMeshBuilder.BuildNavMesh()` requires the active scene to
  already be saved -- the same requirement `bake_lightmaps` found for
  lightmap baking (LightingTools.cs, batch 7), confirmed the same way.
- `UnityEngine.AI.NavMeshBuilder.BuildNavMeshData()` (the runtime/procedural
  NavMesh API, distinct from the Editor-only `UnityEditor.AI.NavMeshBuilder`)
  takes an explicit `NavMeshBuildSettings` by value with no registry-lookup
  indirection, so a custom agent radius/height/slope/step genuinely does
  apply per call -- confirmed via a live round trip (collect sources from a
  bounds, build, `NavMesh.AddNavMeshData`, then `NavMesh.SamplePosition`
  successfully found a point). This is the same mechanism Unity's own
  `NavMeshSurface` component (from the optional `com.unity.ai.navigation`
  package) is built on, achieved here with only core Unity APIs.
  `bake_navmesh_volume` uses this for genuine local/procedural NavMesh
  generation, including re-baking the same `volumeId` to replace rather than
  accumulate duplicate `NavMeshDataInstance`s.
- Custom NavMesh area names/costs (`define_navmesh_area`) are stored in
  `ProjectSettings/NavMeshAreas.asset` (the same file the Navigation
  window's Areas tab edits) as a fixed 32-slot array -- confirmed writable
  via `SerializedObject`, the same technique already established for other
  ProjectSettings-backed features.
- `Undo.AddComponent<T>()` (the pattern used successfully for `Rigidbody` in
  `add_joint`, batch 6) failed for `NavMeshAgent`/`NavMeshObstacle`
  specifically -- the returned component immediately became a stale
  reference, throwing `MissingComponentException` on the very next property
  access. Root cause not fully identified; fixed by using plain
  `GameObject.AddComponent<T>()` instead (also Undo-visible in the Editor,
  just via the component's own creation rather than the Undo-wrapped
  factory), which a live spike confirmed works cleanly for both types.

### Verified
- All 10 new atomic tools were exercised against a real Unity Editor
  instance: a custom area actually created and independently queryable via
  `GameObjectUtility`, updating an existing area's cost, marking a whole
  parent+children hierarchy with a real area index, a genuinely sampleable
  local NavMesh volume bake (including a same-`volumeId` re-bake),
  `sample_navmesh`'s found/not-found paths, a real `NavMeshAgent` with the
  requested radius and area mask, `set_agent_destination` against an agent
  actually warped onto the baked NavMesh (plus a clean-fail check for a bad
  path), a real carving `NavMeshObstacle`, a real `OffMeshLink` with correct
  start/end/biDirectional/costOverride, `configure_navmesh_settings`'
  defaults actually being picked up by a subsequent `bake_navmesh_volume`
  call, and a full `bake_navmesh` run on a minimal saved scene (after
  confirming the clean-fail path on an unsaved one).

## [1.9.0] — Tool catalog expansion, batch 8: Cameras & Cinemachine (new `cameras` group)

### Added
- `create_camera`, `set_camera_properties`, `set_camera_stack`,
  `create_cinemachine_camera`, `set_cinemachine_body`, `set_cinemachine_aim`,
  `trigger_camera_impulse` (new `cameras` group, new `CameraTools.cs`).
  The Cinemachine-dependent tools use reflection against
  `"Cinemachine.*, Cinemachine"` type names (the same optional-package
  pattern `MaterialTools.cs` uses for Shader Graph) so this assembly still
  compiles, and the plain-Camera tools still work, in a project that never
  installed `com.unity.cinemachine`.
- `create_render_texture` (`assets` group, `AssetTools.cs`) — small
  infrastructure tool added to support the `create_render_texture_camera`
  composite below; generically useful for any future camera-to-texture need
  (minimaps, portals).
- `add_camera_shake`, `add_head_bob`, `create_render_texture_camera`
  (`workflows.py` composites, `cameras` group). `add_head_bob` scaffolds a
  small `MCPHeadBob` script the same idempotent way `MCPFlickerLight` is
  scaffolded; it reads a parent `CharacterController`/`Rigidbody`'s speed
  when present and falls back to a subtle idle bob otherwise, so it works
  standalone before the FPS controller (batch 10) exists.
- New `cameras` group registered in `groups.py`.

### Verified against a real installed Cinemachine package, not just its absence
- Unlike Shader Graph/Addressables in earlier batches, Cinemachine 2.10.3 was
  actually installable and resolvable in the verification project (network
  access to the package registry worked), so this batch's optional-package
  tools were verified end-to-end on the real success path, not only the
  "package not installed" failure path:
  - `CinemachineVirtualCamera` can be `AddComponent`'d directly, but its
    Body/Aim components (`CinemachineTransposer`, `CinemachineComposer`,
    etc.) live on a hidden pipeline child GameObject Cinemachine manages
    itself — confirmed via a live spike showing the added component landing
    on a *different* GameObject than the vcam's own. `set_cinemachine_body`
    and `set_cinemachine_aim` therefore call the real
    `CinemachineVirtualCamera.AddCinemachineComponent<T>()` generic method
    via reflection (`MethodInfo.MakeGenericMethod`) rather than a plain
    add_component, which would have silently landed in the wrong place.
  - `CinemachineImpulseSource`'s real method is
    `GenerateImpulseWithForce(float)` (confirmed via reflection over its
    actual method list — several overloads exist, e.g.
    `GenerateImpulseWithVelocity(Vector3)`, `GenerateImpulseAt(Vector3, Vector3)`).
- `set_camera_stack` deliberately avoids URP's `UniversalAdditionalCameraData.cameraStack`
  API (URP isn't installed in the verification project either) in favor of a
  `Camera.depth` + `Depth`-only-clearFlags technique that works in every
  render pipeline, at the cost of URP's extra stacking features.

### Verified
- All 8 new atomic tools were exercised against a real Unity Editor
  instance: a created Camera's real FOV/position, orthographic/size/clearFlags/
  backgroundColor writes read back from the live `Camera` (plus a clean-fail
  check for an unknown culling-mask layer), overlay cameras actually getting
  ascending `depth` values and `Depth` clearFlags, a real, correctly-sized
  `RenderTexture` asset, a real `CinemachineVirtualCamera` with `Follow`
  wired, a real `CinemachineTransposer`/`CinemachineComposer` added to the
  vcam's actual pipeline child (plus a clean-fail check for an unknown body
  type), `LookAt` wired, and `trigger_camera_impulse` auto-adding a real
  `CinemachineImpulseSource` and firing without error.

## [1.8.0] — Tool catalog expansion, batch 7: Lighting (new `lighting` group)

### Added
- `create_light`, `set_light_properties`, `configure_shadows`, `bake_lightmaps`,
  `set_lightmap_settings`, `configure_gi`, `set_ambient_lighting`,
  `create_reflection_probe`, `create_light_probe_group`, `set_skybox`, `set_fog`
  (new `lighting` group, new `LightingTools.cs`).
- `add_flicker_light`, `spawn_emissive_source` (`workflows.py` composites,
  `lighting` group). `add_flicker_light` scaffolds a small `MCPFlickerLight`
  script the same idempotent way `MCPTriggerRelay` is scaffolded.
  `spawn_emissive_source` pairs an emissive-material primitive with a real
  Point Light, since an emissive material alone doesn't illuminate other
  objects in realtime without a lightmap bake.
- New `lighting` group registered in `groups.py`.

### Fixed / discovered (all via live API spikes against a real Unity Editor before writing the tools, not guessed)
- `Light.shadowResolution`'s type is `UnityEngine.Rendering.LightShadowResolution`
  in this Unity version, not the bare `LightShadowResolution` that appears in
  older docs/snippets.
- `UnityEngine.LightingSettings` (the lightmap-baking settings object) is a
  runtime-namespace class, not `UnityEditor.LightingSettings` as in older
  Unity versions.
- `Lightmapping.lightingSettings` -- both the getter *and* setter throw
  `"is null. Please assign it to an existing asset or a new instance"` unless
  the `LightingSettings` instance being assigned has first been saved as a
  real asset via `AssetDatabase.CreateAsset`; a bare unsaved instance can't be
  assigned at all, and reading the getter cold (nothing ever assigned this
  session) throws rather than returning null. `set_lightmap_settings` and
  `configure_gi` now lazily create-and-save a `MCPLightingSettings.lighting`
  asset under `Assets/Settings/` the first time either is called, and treat a
  thrown cold read as "nothing assigned yet" rather than propagating it.
- `Lightmapping.BakeAsync()` + polling `Lightmapping.isRunning` in a busy-wait
  loop never observes completion in batchmode -- the native bake pipeline
  genuinely finishes (confirmed in the Editor log), but the `isRunning` flag
  update apparently needs an `EditorApplication.update` pump tick that a
  blocking `Thread.Sleep` loop on the main thread never yields for. The
  synchronous `Lightmapping.Bake()` call does not have this problem (it
  blocks internally and returns the real result), so `bake_lightmaps` uses
  that instead of the async+poll pattern used elsewhere in this codebase
  (e.g. `wait_for_compile`).
- Baking requires the active scene to already be saved to disk (Unity's own
  requirement, surfaced as a dialog in interactive mode) -- `bake_lightmaps`
  now checks for an empty scene path up front and fails with a clear
  "call save_scene first" message instead of a bare `false` return.
- `LightingSettings.bounces` and `.compressLightmaps` are obsolete
  (`bounces` redirects cleanly to `maxBounces`, applied here); `compressLightmaps`'s
  replacement (`lightmapCompression`, an enum) has unverified value names in
  this Unity version, so the tool keeps the simple obsolete bool API rather
  than guessing enum members.

### Verified
- All 11 new atomic tools were exercised against a real Unity Editor
  instance: a created Point light's real position/type, color/intensity/range
  writes read back from the live `Light`, shadow mode/bias/strength applied
  correctly, ambient Trilight mode with real sky/equator/ground colors and
  intensity, fog enabled with the requested mode/density, a real
  `ReflectionProbe` with requested size/intensity/boxProjection, a real
  `LightProbeGroup` with the requested probe positions (plus a clean-fail
  check for a malformed position string), a real generated
  `Skybox/Procedural` material actually assigned to `RenderSettings.skybox`,
  lightmap settings actually written to the real `LightingSettings` asset,
  GI/reflection settings applied to `RenderSettings`, and a real, successful
  `bake_lightmaps` run on a minimal saved scene (after confirming the
  clean-fail path on an unsaved one).

## [1.7.0] — Tool catalog expansion, batch 6: Materials/Shaders + Physics

### Added
- `set_material_properties`, `assign_material`, `get_material_properties`,
  `list_shaders`, `create_shader_graph`, `inspect_shader_graph`,
  `set_render_queue`, `create_material_variant`, `set_global_shader_property`
  (`assets` group, new `MaterialTools.cs`). `create_shader_graph` and
  `inspect_shader_graph` use reflection against the optional Shader Graph
  package (`com.unity.shadergraph`) and fail with a clear message if it
  isn't installed — only their absence-path was verified here since Shader
  Graph isn't installed in the verification project.
- `spherecast`, `overlap_query`, `add_joint`, `set_layer_collision_matrix`,
  `create_physics_material`, `configure_physics_settings` (`physics` group,
  `PhysicsTools.cs`).
- `add_trigger_volume` (`workflows.py` composite, `physics` group): creates
  a Box/Sphere trigger collider and attaches an idempotently-scaffolded
  `MCPTriggerRelay` component exposing `onTriggerEnter`/`onTriggerExit`
  UnityEvents.

### Fixed
- `PhysicMaterial` is obsolete/hard-error as of this Unity version, renamed
  to `PhysicsMaterial` — caught via a live API spike before writing
  `create_physics_material`, not guessed.
- `create_physics_material` originally accepted (and echoed back) a
  `.physicsMaterial` file extension. A live invoke-test caught that Unity's
  asset importer only recognizes the legacy `.physicMaterial` spelling (no
  "s") for this type even though the runtime class itself was renamed —
  an asset saved with the new spelling silently fails to import as a
  loadable `PhysicsMaterial`. The tool now normalizes the extension instead
  of trusting the caller's spelling.

### Verified
- All 15 new atomic tools and the new composite were exercised against a
  real Unity Editor instance: material color/float property writes read
  back correctly from the live `Material`, `assign_material` produced a
  real `Renderer.sharedMaterial` change, `create_material_variant` produced
  a genuinely independent copy, `set_global_shader_property` was read back
  via `Shader.GetGlobalFloat`, `add_joint(Hinge)` was confirmed to
  auto-add a `Rigidbody`, `set_layer_collision_matrix` was confirmed to
  reject an unknown layer name cleanly, and `create_physics_material`
  was confirmed to produce a real, loadable asset with the requested
  bounciness (after the extension fix above).

## [1.6.0] — Tool catalog expansion, batch 5: Prefabs + Assets/Import

### Added
- `create_prefab_variant`, `open_prefab_mode`, `close_prefab_mode`,
  `apply_prefab_overrides`, `revert_prefab_overrides`, `get_prefab_overrides`,
  `unpack_prefab` (`assets` group, new `PrefabTools.cs`).
- `import_asset`, `move_asset`, `get_asset_dependencies`, `reimport_asset`,
  `set_texture_import_settings`, `set_model_import_settings`, `create_folder`,
  `mark_addressable`, `create_asset_bundle` (`assets` group, `AssetTools.cs`).
  `mark_addressable` uses reflection against the optional Addressables
  package (absent by default) and fails with a clear message if it isn't
  installed — verify against a real project with Addressables set up before
  relying on it.
- `replace_prefab_instances` (`workflows.py` composite): finds every instance
  of one prefab in the scene and swaps each for another, preserving
  transform/parent/name.

### Fixed
- `ModelImporter.importMaterials` is obsolete/read-only as of this Unity
  version (removed in favor of `materialImportMode`) — caught by the real
  compile check, not discovered at runtime. `set_model_import_settings` now
  maps its boolean `importMaterials` parameter onto `materialImportMode`
  (`ImportStandard`/`None`).

### Verified
- All 16 new atomic tools and the new composite were exercised against a
  real Unity Editor instance, not just compiled: creating a genuine Prefab
  Variant (confirmed via `PrefabUtility.GetPrefabAssetType`), a full
  open/edit/close Prefab Mode round trip that persists the edit, prefab
  override apply/revert/unpack against a real instance, texture import
  settings actually changing a real `TextureImporter`, and a real
  `BuildPipeline.BuildAssetBundles` output file on disk. One interesting,
  confirmed-not-a-bug finding along the way: `PrefabUtility.RevertPrefabInstance`
  also reverts an instance's custom *name* back to the source prefab's name,
  since a renamed instance is itself an override — expected Unity behavior,
  not a defect in `revert_prefab_overrides`.

## [1.5.0] — Tool catalog expansion, batch 4: GameObject/Transform + Components

### Added
- `rename_gameobject`, `set_gameobject_active`, `set_gameobject_static`,
  `get_transform`, `translate_gameobject` (`core` group, `SceneTools.cs`).
- `get_component_properties`, `set_component_properties_batch`,
  `wire_object_reference`, `batch_wire_references`, `copy_component`,
  `find_missing_components` (`core` group, `ComponentTools.cs`). Extracted the
  shared field-reflection logic `get_component_field`/`set_component_field`
  already had into `MCPComponentReflection.cs` so the new tools reuse it
  instead of duplicating it.
- `align_gameobjects`, `snap_to_ground` (`workflows.py` composites). Both move
  objects via `translate_gameobject`'s world-space mode rather than
  `set_transform`'s local-only position, so they're correct for objects with
  non-identity parents, not just scene-root objects.

### Fixed
- `set_active_scene`'s "the real API returns false even on success" bug (see
  1.4.0) was one instance of a broader lesson applied throughout this batch:
  `copy_component` uses Unity's own `ComponentUtility.CopyComponent`/
  `PasteComponentValues` (the same mechanism the Inspector's own "Copy
  Component"/"Paste Component Values" commands use) rather than a hand-rolled
  reflection copy, specifically so private `[SerializeField]` fields and
  complex types are handled exactly as Unity itself handles them.
- `align_gameobjects`/`snap_to_ground` are registered under the default
  `core` group (always active, per the source catalog's own group mapping),
  which meant they now always appear in `list_tools()` — four Python tests
  (`test_tool_groups.py`, `test_server_handlers.py`,
  `test_behavior_tree_workflows.py`, `test_reconnect_notifies_client.py`) had
  hardcoded exact tool-set assertions that didn't account for that. Updated
  all four. One of them additionally exposed a real hang-on-failure footgun:
  when such an assertion fails inside `async with fake_server:` before
  `bridge.close()` runs, the client's still-open connection keeps
  `fake_server`'s `__aexit__`/`wait_closed()` blocked forever instead of the
  test failing with a clean traceback — worth keeping in mind for any future
  test in this file that asserts before closing the bridge.

## [1.4.0] — Tool catalog expansion, batch 3: Scene Management

### Added
- New `scene` group: `open_scene`, `save_scene`, `create_scene`, `close_scene`,
  `get_scene_hierarchy`, `set_active_scene`, `merge_scenes`,
  `list_scenes_in_build`, `add_scene_to_build`, `get_scene_stats`.

### Fixed
- `set_active_scene` used `UnityEngine.SceneManagement.SceneManager.SetActiveScene`
  (the runtime/Play-mode/build API), which — confirmed against a real Editor
  instance — can actually change the active scene while still returning `false`
  when called from the Editor outside Play mode, making the tool wrongly report
  failure for a call that had, in fact, succeeded. Switched to
  `UnityEditor.SceneManagement.EditorSceneManager.SetActiveScene` (the
  Editor-context API), with success confirmed afterward by checking the active
  scene actually changed, since that method returns `void`.
- `python/tests/test_tool_groups.py` asserted an exact 6-group set that was
  already stale (missing the existing `inspection`/`testing` groups) before this
  batch's `scene` group made the drift one group worse; updated to the current
  9-group set.

## [1.3.0] — Tool catalog expansion, batch 2: Editor Control & Session

### Added
- `get_editor_state`, `execute_menu_item`, `undo`, `redo`, `get_undo_stack`,
  `set_editor_selection`, `get_editor_selection`, `focus_scene_view`,
  `list_unity_instances` (`core` group).

### Deliberately not added
- `run_csharp` (compile-and-run an arbitrary C# snippet), present in the source
  wishlist's Editor Control group. Unlike every other tool, arbitrary code
  execution bypasses every existing safety mechanism at once (the destructive/
  confirm gate, the Assets/-only path guard, the rate limiter) since compiled
  code can do anything the Editor process can do, not just what one tool's
  method body does. Skipped rather than shipped in any form.

## [1.2.0] — Tool catalog expansion, batch 1: inspection + scripting completion

Start of an ongoing build-out toward the full tool catalog in
`unity-mcp-300-tools-fps-horror.md` (docs/tool-catalog.md tracks progress
batch-by-batch). This batch completes the two groups already closest to done.

### Added
- `resolve_type`, `list_assembly_definitions`, `create_assembly_definition`,
  `update_assembly_definition` (`scripting` group).
- `capture_from_camera`, `draw_debug_gizmo`, `get_frame_debugger_info`,
  `capture_editor_window`, `get_object_screen_bounds` (`inspection` group).
  `get_frame_debugger_info` and `capture_editor_window` use Unity-internal APIs
  (accessed via reflection specifically because the underlying type is
  inaccessible directly) and can't be fully exercised headless — verify with a
  real, visible Editor session before relying on them.
- `MCPToolRegistry` now supports `string[]`-typed tool parameters end-to-end
  (schema generation and wire-argument coercion) — previously unsupported
  entirely, which would have blocked most of the remaining catalog (references,
  tags, bindings, and similar list-shaped parameters are pervasive in it).

## [1.1.0] — Tool Builder removed; Setup window absorbs its settings, gains history + color

### Removed
- The visual Tool Builder (`Window → Unity MCP → Tool Builder` and everything
  under `Editor/ToolBuilder/`: `MCPCompositeToolGenerator`,
  `MCPCompositeToolSpec`, `MCPToolBuilderWindow`, `MCPToolBuilderSettings`).
  Composite tools are still fully supported — write a Python
  `@workflow`-decorated function directly in `custom_workflows.py` (see
  `docs/writing-custom-tools.md` §11) — just without the no-code form UI.

### Added
- The Python server location field (formerly only editable from the removed
  Tool Builder window) now lives directly in `Window → Unity MCP → Setup`.
  The EditorPrefs key is unchanged, so anyone upgrading keeps whatever path
  they'd already configured.
- The Setup window now shows which client (Claude Code / Codex / Cursor /
  Antigravity) was configured most recently, and when (`MCPClientConfigTracker`,
  persisted via EditorPrefs so it survives Editor restarts, not just domain
  reloads).
- Color-coded sections throughout the Setup window: green for a running
  bridge / no conflict / a configured client, yellow for something that needs
  attention (a conflict, or no Python server location set yet), red for the
  instance-lock-blocked state, with a small colored dot on each client row.

## [1.0.1] — Multi-instance lock, duplicate-tool cleanup, disk-growth hardening

### Fixed
- **The reported "MCP silently fails mid-use, Setup window shows another
  instance's PID" incident.** `MCPServer.Start()` used to overwrite
  `session.json` unconditionally even after `MCPInstanceConflictDetector`
  flagged a live conflict — with two live Unity processes for the same
  project, both kept re-winning that race on every domain reload, silently
  redirecting an already-connected MCP client to whichever one wrote last,
  with no error at all. Added `MCPInstanceLock`, an OS-level exclusive file
  lock (`Library/MCP/bridge.lock`) acquired before `session.json` is ever
  written: only the process holding it is allowed to claim the bridge for
  this project. A process that loses the race now refuses to listen or
  publish a session file at all, logs a clear `Debug.LogError`, and the Setup
  window shows an unmissable error with a **Retry** button — instead of a
  passive warning label next to an otherwise-normal-looking "Running" status.
  This also closes a PID-reuse false-positive the old detector was exposed
  to (a stale PID reused by an unrelated process would read back as
  "still alive" forever); the file lock is released by the OS the instant
  its owning process exits, for any reason, so there is no reuse window.
- **3 duplicate tool registrations**: `enter_play_mode`, `exit_play_mode`,
  and `pause_play_mode` were each defined twice (`EditorStateTools.cs` *and*
  `PlayModeTools.cs`). `MCPToolRegistry.Rescan()` silently dropped whichever
  definition it scanned second, so which behavior was actually live could
  flip depending on assembly/type enumeration order — including a naive
  version that returned immediately instead of waiting for the Play Mode
  transition (and any domain reload it triggers) to settle, which is one
  concrete way a tool call right after `enter_play_mode` could hit the
  bridge mid-teardown and drop. Removed the duplicates from
  `EditorStateTools.cs`, keeping only `PlayModeTools.cs`'s wait-for-settle
  versions (note: its `pause_play_mode` parameter is named `paused`, not the
  removed version's `pause`).
- **"Setup window shows 0 clients right after Play Mode, but the AI agent
  still shows connected"**: this reading was technically correct (entering/
  exiting Play Mode triggers a full domain reload by default, which wipes
  `MCPServer`'s static state — including the connected-client count — and
  drops the bridge's live socket; the MCP client's Python process itself
  stays up and reconnects transparently on its next tool call, which is why
  the AI tool never appeared to notice), but the Setup window had no way to
  distinguish "0 because a restart just happened and a client will reconnect
  automatically" from "0 because nobody has ever connected" or "0 and
  something is actually wrong" — it looked identical either way. Added
  `MCPServer.HadClientBeforeLastRestart` (persisted via `SessionState`
  specifically so it survives the domain reload that a plain field would be
  wiped by) and `SecondsSinceStart`; the Setup window now shows a plain blue
  informational note for about 20 seconds after a restart that had a client
  connected right before it, explaining the reconnect is automatic and
  expected — not a warning, since nothing is actually wrong.

### Security
- `MCPAuditLog` (`Library/MCP/audit.log`) grew forever with no rotation — a
  long-running agent session logging thousands of tool calls had no natural
  cap. Now rotates to a single `audit.log.1` backup at 5MB.
- `MCPScreenshotUtil` (`<project>/MCPScreenshots/`) kept every
  `capture_scene_view`/`capture_game_view` PNG forever. Now prunes down to
  the 50 most recent captures after every new one.
- `MCPAuthHandshake` tokens are now generated with a cryptographically
  secure RNG (`RandomNumberGenerator`) instead of `Guid.NewGuid()`, and
  compared in constant time to avoid a timing side-channel on handshake
  validation.

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
