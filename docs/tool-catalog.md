# Unity MCP Tool Catalog

Living reference of every tool this MCP server exposes, organized by group (see
`Window → Unity MCP → Setup` and `manage_tools` for how groups control visibility —
`core` is always active, everything else is opt-in per session).

This file is updated as tool groups are built out toward the full catalog in
`unity-mcp-300-tools-fps-horror.md`. Each entry lists the tool's **Type**
(`A` = atomic, wraps one Unity API directly in C#; `C` = composite, a Python
`@workflow` orchestrating one or more atomic tools) and where it's implemented.

**Status:** 312 tools implemented (231 atomic C# + 81 composite Python) across 26 groups —
the full `unity-mcp-300-tools-fps-horror.md` catalog is now built out end to end.
Build progress toward the full `unity-mcp-300-tools-fps-horror.md` catalog is tracked
batch-by-batch at the bottom of this file.

---

## `core` — always active

Essential scene/component/query tools plus session-management composites. Every
session sees these regardless of `manage_tools` state.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_gameobject` | A | `SceneTools.cs` | Creates a new empty GameObject in the active scene, optionally under a parent hierarchy path. |
| `delete_gameobject` | A | `SceneTools.cs` | Deletes a GameObject by hierarchy path. Destructive — requires `confirm: true`. |
| `duplicate_gameobject` | A | `SceneTools.cs` | Duplicates a GameObject (and its children) by hierarchy path. |
| `set_transform` | A | `SceneTools.cs` | Sets local position/rotation(euler)/scale on a GameObject by path. Omitted axes are left unchanged. |
| `reparent_gameobject` | A | `SceneTools.cs` | Moves a GameObject under a new parent path, or to scene root if omitted. |
| `find_gameobjects` | A | `SceneTools.cs` | Finds GameObjects in the active scene by exact name and/or tag. |
| `create_primitive` | A | `PrimitiveTools.cs` | Creates a standard 3D primitive (Cube, Sphere, Capsule, Cylinder, Plane, Quad) with optional position. |
| `add_component` | A | `ComponentTools.cs` | Adds a component to a GameObject by path and type name. |
| `remove_component` | A | `ComponentTools.cs` | Removes a component from a GameObject by path and type name. |
| `list_components` | A | `ComponentTools.cs` | Lists the full type names of every component on a GameObject. |
| `get_component_field` | A | `ComponentTools.cs` | Reads a public field/property value from a component by path, type, and member name. |
| `set_component_field` | A | `ComponentTools.cs` | Sets a public field/property on a component; value is coerced to the member's actual type. |
| `get_hierarchy` | A | `QueryTools.cs` | Returns the full GameObject hierarchy of the active scene as a nested tree (cached, invalidated on structural changes). |
| `get_selected_object` | A | `QueryTools.cs` | Returns the hierarchy path of the currently selected GameObject, or null. |
| `get_console_logs` | A | `QueryTools.cs` | Returns the most recent N console log entries since the Editor last started. |
| `get_project_info` | A | `QueryTools.cs` | Returns Unity version, active scene name/path, and build target. |
| `save_project` | A | `EditorStateTools.cs` | Saves all modified assets and all currently open scenes (Ctrl/Cmd+S equivalent). |
| `get_editor_state` | A | `EditorControlTools.cs` | Reports play/pause/compiling/updating flags, active scene name/path/dirty, and selection count. |
| `execute_menu_item` | A | `EditorControlTools.cs` | Invokes any Unity Editor menu item by exact path — escape hatch for un-wrapped features. |
| `undo` | A | `EditorControlTools.cs` | Performs one Editor Undo step. |
| `redo` | A | `EditorControlTools.cs` | Performs one Editor Redo step. |
| `get_undo_stack` | A | `EditorControlTools.cs` | Reports the current undo group's index and name (Unity's public API doesn't expose full history). |
| `set_editor_selection` | A | `EditorControlTools.cs` | Sets the Editor selection to given scene GameObjects and/or project assets by path. |
| `get_editor_selection` | A | `EditorControlTools.cs` | Returns the current selection as GameObject hierarchy paths and/or asset paths. |
| `focus_scene_view` | A | `EditorControlTools.cs` | Frames the Scene view camera on a target GameObject (and selects it). **Manual Test**: needs a real, open Scene view. |
| `list_unity_instances` | A | `EditorControlTools.cs` | Reports this instance's own PID/port/project plus a count of other Unity-named processes running. |
| `rename_gameobject` | A | `SceneTools.cs` | Renames a GameObject by hierarchy path. |
| `set_gameobject_active` | A | `SceneTools.cs` | Enables or disables a GameObject. |
| `set_gameobject_static` | A | `SceneTools.cs` | Sets static flags (batching/navigation/occlusion/reflection-probe/GI), granular or all-at-once. |
| `get_transform` | A | `SceneTools.cs` | Reads a GameObject's transform in both local and world space. |
| `translate_gameobject` | A | `SceneTools.cs` | Moves a GameObject by a delta vector, in local (unrotated) or world space. |
| `get_component_properties` | A | `ComponentTools.cs` | Reads every public field/property on a component in one call; object refs reduced to path/name. |
| `set_component_properties_batch` | A | `ComponentTools.cs` | Sets multiple fields on one component atomically (validated before any are written). |
| `wire_object_reference` | A | `ComponentTools.cs` | Assigns a scene GameObject/component or project asset into a component's reference field. |
| `batch_wire_references` | A | `ComponentTools.cs` | Wires multiple object-reference fields (JSON-spec array) atomically in one call. |
| `wire_unity_event` | A | `ComponentTools.cs` | Adds a persistent listener (via `UnityEditor.Events.UnityEventTools`, the same mechanism the Inspector's own '+' button uses) to a `UnityEvent`/`UnityEvent<T>` field, calling a method on another component. Auto-instantiates the event field if it's still null (a real gotcha: `AddComponent()` this session leaves `UnityEvent` fields null, unlike the Inspector's own "Add Component"), defaults the new listener's call state to `EditorAndRuntime` instead of Unity's own `RuntimeOnly` default so wiring can be verified by invoking the event directly in the Editor, defaults to a **dynamic** listener that forwards the event's real runtime argument (`dynamic: false` bakes a fixed constant instead — confirmed via live spike these need genuinely different `UnityEventTools` overloads, not just a parameter toggle), and falls back to a "static" parameterless listener if `methodName` has no overload matching the event's own generic argument at all (e.g. wiring a plain `Activate()` to a `UnityEvent<Collider>` like `MCPTriggerRelay`'s `onTriggerEnter`). |
| `copy_component` | A | `ComponentTools.cs` | Copies a component's full field state onto another GameObject via Unity's own Copy/Paste Component Values. |
| `find_missing_components` | A | `ComponentTools.cs` | Scans a scene for GameObjects with missing/broken ("Missing (Mono Script)") components. |
| `batch_execute` | C | `workflows.py` | Sends N `{tool, args}` calls in one wire round trip; results returned in order. |
| `manage_tools` | C | `workflows.py` | Lists/activates/deactivates tool groups for this session (`list_groups`/`activate`/`deactivate`/`reset`). |
| `align_gameobjects` | C | `workflows.py` | Aligns or evenly distributes GameObjects along a world axis, via world-space translation (parent-safe). |
| `snap_to_ground` | C | `workflows.py` | Moves a GameObject down onto the first collider hit below it, with an optional clearance offset. |

## `scene` — multi-scene management

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `open_scene` | A, Slow | `SceneManagementTools.cs` | Opens a scene by path, Single (replaces loaded scenes) or Additive. |
| `save_scene` | A | `SceneManagementTools.cs` | Saves a loaded scene by name (or the active scene) to disk. |
| `create_scene` | A, Slow | `SceneManagementTools.cs` | Creates a new empty scene and saves it as an asset; becomes the active scene. |
| `close_scene` | A | `SceneManagementTools.cs` | Unloads an additively loaded scene by name. |
| `get_scene_hierarchy` | A | `SceneManagementTools.cs` | Nested hierarchy tree for any loaded scene, with root-level pagination and an optional depth limit. |
| `set_active_scene` | A | `SceneManagementTools.cs` | Sets which loaded scene new GameObjects instantiate into by default. |
| `merge_scenes` | A, Slow, destructive | `SceneManagementTools.cs` | Merges one loaded scene into another, then unloads the source. |
| `list_scenes_in_build` | A | `SceneManagementTools.cs` | Lists scenes registered in Build Settings, in build order, with enabled state. |
| `add_scene_to_build` | A | `SceneManagementTools.cs` | Adds a scene to Build Settings at a given index; fails rather than duplicates if already present. |
| `get_scene_stats` | A | `SceneManagementTools.cs` | GameObject/vertex/light/collider counts for a loaded scene. |

## `lighting` — lights, shadows, lightmapping/GI, ambient, probes, skybox, fog **[GENRE — horror atmosphere lives here]**

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_light` | A | `LightingTools.cs` | Creates a GameObject with a Light component (Directional/Point/Spot/Rectangle/Disc). |
| `set_light_properties` | A | `LightingTools.cs` | Sets color/intensity/range/spot angle/cookie/shadow mode on a Light. |
| `configure_shadows` | A | `LightingTools.cs` | Configures shadow type/resolution/bias/strength for a specific Light. |
| `bake_lightmaps` | A **[LOOP]**, Slow | `LightingTools.cs` | Synchronously bakes lightmaps for the active scene. Fails clearly if the scene hasn't been saved yet (a real Unity requirement, not a limitation of this tool). |
| `set_lightmap_settings` | A | `LightingTools.cs` | Configures lightmapper/resolution/padding/max size/bounces/AO/denoiser for baking. |
| `configure_gi` | A | `LightingTools.cs` | Configures realtime/baked GI (indirect scale, albedo boost) and environment reflection settings. |
| `set_ambient_lighting` | A | `LightingTools.cs` | Configures ambient mode (Flat/Trilight/Skybox/Custom), sky/equator/ground colors, and intensity — the main "darkness" control. |
| `create_reflection_probe` | A | `LightingTools.cs` | Creates a GameObject with a Reflection Probe (Baked/Realtime/Custom). |
| `create_light_probe_group` | A | `LightingTools.cs` | Creates a GameObject with a Light Probe Group at given local positions, for correct lighting on dynamic objects. |
| `set_skybox` | A | `LightingTools.cs` | Assigns an existing skybox material, or generates+assigns a new procedural sky material. |
| `set_fog` | A | `LightingTools.cs` | Enables/tunes distance fog. Height fog is a per-pipeline Volume override, out of scope here — see the future `rendering` group. |
| `add_flicker_light` | C | `workflows.py` | Attaches a scaffolded `MCPFlickerLight` component that randomizes a Light's intensity every frame (failing-bulb/strobe dread). |
| `spawn_emissive_source` | C | `workflows.py` | Creates a primitive with an emissive material plus a real Point Light nearby, so it actually illuminates (emissive materials alone don't light other objects in realtime). |

## `cameras` — cameras, Cinemachine, camera shake, render-texture cameras

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_camera` | A | `CameraTools.cs` | Creates a GameObject with a plain Camera component. |
| `set_camera_properties` | A | `CameraTools.cs` | Sets FOV/clip planes/projection/clear behavior/background color/culling mask on an existing Camera. |
| `set_camera_stack` | A | `CameraTools.cs` | Orders overlay cameras via `Camera.depth` + `Depth`-only clearFlags — a render-pipeline-agnostic technique (works without needing URP's dedicated stacking API). |
| `create_cinemachine_camera` | A | `CameraTools.cs` | Creates a GameObject with a CinemachineVirtualCamera, optionally wiring Follow/LookAt. Requires the Cinemachine package (com.unity.cinemachine); reflection-based, fails clearly if absent. |
| `set_cinemachine_body` | A | `CameraTools.cs` | Configures a vcam's Body stage (Transposer/FramingTransposer/ThirdPersonFollow/HardLockToTarget/TrackedDolly/OrbitalTransposer) via the real `AddCinemachineComponent<T>()` API — body/aim components live on a hidden pipeline child Cinemachine manages itself, so a plain add_component wouldn't land in the right place. |
| `set_cinemachine_aim` | A | `CameraTools.cs` | Configures a vcam's Aim stage (Composer/GroupComposer/POV/HardLookAt/SameAsFollowTarget) the same way. |
| `trigger_camera_impulse` | A **[LOOP]** | `CameraTools.cs` | Fires a one-shot Cinemachine impulse (camera shake) from a GameObject, auto-adding a CinemachineImpulseSource if missing. |
| `add_camera_shake` | C | `workflows.py` | Wires a CinemachineImpulseSource (and optionally a CinemachineImpulseListener on the Brain camera) — call trigger_camera_impulse afterward to fire it. |
| `add_head_bob` | C | `workflows.py` | Attaches a scaffolded `MCPHeadBob` component that bobs a first-person camera based on a parent CharacterController/Rigidbody's speed (idle bob if neither exists yet). |
| `create_render_texture_camera` | C | `workflows.py` | Creates a Camera rendering into a new RenderTexture, optionally displayed on an existing material — for CCTV/monitor/portal props. |

Cinemachine-dependent tools (`create_cinemachine_camera`, `set_cinemachine_body`, `set_cinemachine_aim`,
`trigger_camera_impulse`, `add_camera_shake`) were verified against a real installed Cinemachine 2.10.3
package in the verification project (not just the "package absent" failure path) — see the 1.9.0 changelog
entry for the API details confirmed along the way.

## `navmesh` — NavMesh baking, agents, obstacles, links, areas

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `bake_navmesh` | A **[LOOP]**, Slow | `NavMeshTools.cs` | Bakes every `NavMeshSurface` in the active scene (creating one that collects all objects if the scene has none), and saves the resulting NavMeshData as an asset beside the scene. Fails clearly if the scene hasn't been saved yet (a real Unity requirement, same as bake_lightmaps). |
| `configure_navmesh_settings` | A | `NavMeshTools.cs` | Sets this server's session-default agent radius/height/max-slope/step-height, used by bake_navmesh_volume. Unity has no public API to modify an existing agent type's settings (confirmed via reflection), so this can't affect bake_navmesh. |
| `add_navmesh_agent` | A | `NavMeshTools.cs` | Adds and configures a NavMeshAgent (radius/height/speed/areaMask/etc). |
| `set_agent_destination` | A **[LOOP]**, Slow | `NavMeshTools.cs` | Commands an agent toward a point; reports pathStatus, for both movement and reachability testing. |
| `add_navmesh_obstacle` | A | `NavMeshTools.cs` | Adds and configures a NavMeshObstacle (Box/Capsule, optionally carving). |
| `create_offmesh_link` | A | `NavMeshTools.cs` | Creates a GameObject with a `NavMeshLink` connecting two points, for jump/climb/vault gaps. (Tool name kept for compatibility; the legacy `OffMeshLink` component it used to create is deprecated in Unity 6.) |
| `define_navmesh_area` | A | `NavMeshTools.cs` | Creates/updates a named NavMesh area type with a traversal cost, via `ProjectSettings/NavMeshAreas.asset`. |
| `mark_navmesh_area` | A | `NavMeshTools.cs` | Sets a GameObject's (and by default its children's) NavMesh area type by adding a `NavMeshModifier`. Re-calling updates the existing component rather than stacking duplicates. (Replaces the deprecated `GameObjectUtility.SetNavMeshArea`.) |
| `sample_navmesh` | A | `NavMeshTools.cs` | Finds the nearest valid NavMesh point to a world position. |
| `bake_navmesh_volume` | A, Slow | `NavMeshTools.cs` | Bakes a local NavMesh from real scene geometry within a bounds box, via `NavMeshBuilder.BuildNavMeshData()` -- genuinely respects custom agent radius/height/slope/step per call, unlike bake_navmesh. Re-baking the same `volumeId` replaces rather than duplicates. |

## `fps_controller` — first-person player rig **[GENRE]**

Unity has no built-in FPS controller (`CharacterController` is just the physics capsule); all movement/look/
utility logic here is hand-written and scaffolded the same idempotent way as `MCPTriggerRelay`. Ground movement,
sprint, crouch, and jump all live in one `MCPFPSController` component (they're too tightly coupled to split --
one `CharacterController.Move()` call per frame drives all of them); look/footsteps/interaction/stamina/
flashlight/lean are separate, decoupled scripts. Movement/look inputs (`moveInput`, `lookInput`, `jumpRequested`)
are public fields meant to be driven by an input system (batch 17) -- these scripts don't read Input themselves.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `add_character_controller` | A | `CharacterControllerTools.cs` | Adds and configures a CharacterController (radius/height/center/slopeLimit/stepOffset/skinWidth). |
| `create_fps_player` | C | `workflows.py` | Assembles a full rig in one call: CharacterController + child camera + `MCPFPSController` + `MCPMouseLook`, all wired. |
| `configure_ground_movement` | C | `workflows.py` | Tunes `MCPFPSController`'s walkSpeed/acceleration/friction. |
| `configure_sprint` | C | `workflows.py` | Tunes `MCPFPSController`'s sprintSpeed/isSprinting. |
| `configure_crouch` | C | `workflows.py` | Tunes `MCPFPSController`'s crouchHeight/crouchSpeed/standUpClearanceCheck (a real capsule-overlap check before standing). |
| `configure_jump` | C | `workflows.py` | Tunes `MCPFPSController`'s jumpHeight/gravity/coyoteTime. |
| `add_head_look` | C | `workflows.py` | Attaches/tunes `MCPMouseLook` (body yaw + camera pitch with clamp). |
| `add_footstep_system` | C | `workflows.py` | Attaches `MCPFootsteps` (interval-based footstep audio while grounded and moving; single default clip only, no per-surface detection yet). |
| `add_interaction_raycaster` | C | `workflows.py` | Attaches `MCPInteractionRaycaster` + scaffolds the `IInteractable` interface; fires found/lost UnityEvents, exposes `TryInteract()`. |
| `add_stamina_system` | C | `workflows.py` | Attaches `MCPStamina`, a general-purpose drain/regen resource with depleted/regenerated UnityEvents. |
| `add_flashlight` | C | `workflows.py` | Creates a child Spot light + attaches `MCPFlashlight` (toggleable, battery drain, low-battery flicker). |
| `add_lean_system` | C | `workflows.py` | Attaches `MCPLean` (smoothed peek-lean offset/tilt via `LeanLeft()`/`LeanRight()`/`LeanNone()`). |

**Manual Test**: all 7 scaffolded scripts were confirmed to compile in a real Unity Editor, and every field/wiring
path each composite uses was verified against real components (correct field names/types, correct object-reference
wiring) -- but actual movement *feel* (jump arc, coyote-time window, mouse-look responsiveness) can only be judged
by entering Play mode in a real project with real input, not headlessly.

## `weapons` — weapons & combat **[GENRE]**

Every tool in this group is a Python composite -- no new atomic C# tool was needed, since everything is built by
orchestrating already-existing generic tools (`create_gameobject`, `add_component`, `set_component_properties_batch`,
`wire_object_reference`, `create_light`, `create_primitive`, `create_prefab`, `add_collider`) against a handful of
scaffolded scripts, the same idempotent way as prior batches. `IDamageable` and `MCPHitReaction` are shared
foundation scripts that multiple composites scaffold defensively (whichever gets called first wins, matching the
`IInteractable`/`MCPInteractionRaycaster` pattern from `fps_controller`). Note: `wire_object_reference`/
`batch_wire_references` only wire a single object reference per field, not array elements -- `MCPWeaponSwitcher`
is designed around that constraint by auto-discovering its direct children as weapon slots at runtime instead of
needing an explicit `GameObject[]` wired in.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_weapon` | C | `workflows.py` | Scaffolds a weapon rig: GameObject + optional primitive placeholder model + an offset `Muzzle` child transform. No separate weapon-data asset -- the other tools configure runtime components directly. |
| `configure_hitscan` | C | `workflows.py` | Attaches/tunes `MCPHitscanWeapon`: instant raycast fire with damage/range/spread/fireRate. |
| `configure_projectile` | C | `workflows.py` | Attaches/tunes `MCPProjectileWeapon`; auto-creates a minimal default projectile prefab (trigger sphere + `MCPProjectile`) if none is given. |
| `add_ammo_system` | C | `workflows.py` | Attaches/tunes `MCPAmmoSystem`: magazine/reserve/reload timing with onReloadStarted/onReloadFinished/onEmpty events. |
| `add_recoil` | C | `workflows.py` | Attaches/tunes `MCPRecoil`: per-shot rotational kick with smoothed recovery. |
| `add_muzzle_flash` | C | `workflows.py` | Creates a Point light at the muzzle + attaches `MCPMuzzleFlash` (a brief light pop on `Flash()`). No particle VFX yet -- lands with the `vfx` group. |
| `add_weapon_sway` | C | `workflows.py` | Attaches/tunes `MCPWeaponSway`: idle sine sway plus optional look-input-driven sway. |
| `add_hit_reaction` | C | `workflows.py` | Attaches/tunes `MCPHitReaction` on a hittable target: spawns an impact prefab and/or sound, looked up automatically by hitscan/projectile/melee weapons. |
| `add_melee_attack` | C | `workflows.py` | Attaches/tunes `MCPMeleeAttack`: sphere-cast arc attack with damage and cooldown. |
| `create_damage_receiver` | C | `workflows.py` | Attaches `MCPHealth` (implements `IDamageable`) and, optionally, `MCPHitZone` on a child collider with its own damage multiplier (e.g. headshots). |
| `add_weapon_switching` | C | `workflows.py` | Attaches `MCPWeaponSwitcher` to an inventory holder; its direct children are auto-discovered as weapon slots at runtime. |

**Manual Test**: `MCPMuzzleFlash.Flash()` and `MCPWeaponSwitcher`'s child-discovery both run from `Awake()`/method
calls that need Play mode to observe (Awake() isn't guaranteed to have run yet immediately after `AddComponent` in
Edit mode) -- structure, fields, and wiring were all verified for real; `MCPHitscanWeapon.TryFire()` and
`MCPMeleeAttack.TryAttack()` were verified end-to-end against a real `IDamageable` target (confirmed actual damage
applied), since those don't depend on `Awake()`.

## `enemy_ai` — enemy actors, senses, patrol, spawners **[GENRE]**

Every "behavior" here is a state (Idle/Patrol/Chase/Search/Attack/Stalk) on one consolidated `MCPEnemyBrain`
component, not a Sequence/Selector Behavior Tree -- same reasoning as `MCPFPSController` in `fps_controller`
(these states are tightly coupled and mutually exclusive, sharing one `NavMeshAgent.SetDestination()` call per
frame). The source catalog's `scaffold_behavior_tree`/`add_bt_node`/`connect_bt_nodes` are treated as already
covered by the existing `behavior_tree` group's `scaffold_behavior_tree_framework`/`create_behavior_tree`/
`add_behavior_tree_node` rather than shipping a second, overlapping tree mechanism. `MCPEnemyBrain.PerformAttack()`
calls `TryAttack()`/`TryFire()` via `SendMessage` (not a direct type reference) so it compiles in projects that
never used the `weapons` group at all.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_enemy` | C | `workflows.py` | Assembles an enemy: GameObject + NavMeshAgent + optional primitive placeholder model + `MCPHealth` + `MCPEnemyBrain`, optionally with sight/hearing sensors. |
| `add_sight_sensor` | C | `workflows.py` | Attaches/tunes `MCPSightSensor`: FOV + raycast line-of-sight, calling the brain's `OnTargetDetected`/`OnTargetLost`. |
| `add_hearing_sensor` | C | `workflows.py` | Attaches/tunes `MCPHearingSensor`, reacting to the static `MCPNoiseEvents.Emit(position, radius)` bus. Nothing emits into it yet -- wiring a real noise source (footsteps, gunfire) is a manual follow-up. |
| `add_patrol_route` | C | `workflows.py` | Creates ordered waypoint children under a `PatrolRoute` holder and wires the brain's `patrolRouteParent` to it; the brain auto-discovers direct children as waypoints (no tool-level way to wire a `Transform[]` array, same constraint `MCPWeaponSwitcher` worked around). |
| `configure_chase_behavior` | C | `workflows.py` | Tunes `MCPEnemyBrain`'s chaseSpeed/attackRange. |
| `configure_search_behavior` | C | `workflows.py` | Tunes `MCPEnemyBrain`'s searchDuration. |
| `configure_attack_behavior` | C | `workflows.py` | Tunes `MCPEnemyBrain`'s attackRange/telegraphDuration (a wind-up delay before the hit lands, with an `onAttackTelegraphed` UnityEvent for a visible tell). |
| `add_stalker_ai` | C | `workflows.py` | Enables/tunes `MCPEnemyBrain`'s stalker behavior: retreat when close/seen, approach when far/unseen. |
| `add_enemy_spawner` | C | `workflows.py` | Creates (or reuses) a GameObject with `MCPEnemySpawner`: spawns a wave of a prefab in a radius; call `StartWave()` externally (no automatic trigger-condition wiring). |

**Manual Test**: `MCPEnemyBrain`'s Update()-loop movement (patrol waypoint cycling, chase/search/stalk NavMesh
pursuit) and `MCPSightSensor`/`MCPHearingSensor`'s detection callbacks all depend on `Awake()` having populated
`_agent`/`_patrolPoints`/the brain reference, which isn't guaranteed immediately after `AddComponent()` outside
Play mode -- structure, fields, and wiring were all verified for real; `SetState()` (which doesn't touch
`Awake()`-populated fields) and the `MCPBlackboard` JSON round trip were verified end-to-end.

## `audio` — sources, spatial audio, mixer, reverb, occlusion, ambience, dynamic music **[GENRE]**

`create_audio_mixer` drives `UnityEditor.Audio.AudioMixerController` entirely via reflection -- it's an internal
class in a core Unity assembly (no optional package involved, unlike Cinemachine/Shader Graph), and live spikes
found its `CreateNewGroup`/`AddGroupToCurrentView` don't reliably attach a new group into the mixer's persisted
group tree without further internal Editor view-state setup (`AddGroupToCurrentView` threw
`IndexOutOfRangeException` on a fresh controller). Rather than keep reverse-engineering increasingly fragile
internal state, the tool is scoped to creating the mixer asset with its default Master group only -- additional
groups and exposed parameters need the Mixer window's own UI, and `set_mixer_parameter` can only touch a
parameter that's already been exposed there. `add_footstep_audio_set` ships a fully self-contained
`MCPSurfaceFootsteps` rather than extending `fps_controller`'s `MCPFootsteps` -- the same cross-batch
compile-time-coupling avoidance `MCPEnemyBrain` used via `SendMessage`, here solved by duplication instead since
there's no method to call.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `add_audio_source` | A | `AudioTools.cs` | Adds and configures a 3D `AudioSource` (spatial blend, min/max distance). |
| `set_audio_source_properties` | A | `AudioTools.cs` | Sets clip/volume/pitch/loop/priority/playOnAwake/mute on an existing `AudioSource`; omitted params left unchanged. |
| `configure_spatial_audio` | A | `AudioTools.cs` | Configures rolloff mode, distance range, spread, and Doppler level on an existing `AudioSource`. |
| `add_audio_listener` | A | `AudioTools.cs` | Adds an `AudioListener`; reports `alreadyPresent` and every other listener's path in the scene (doesn't remove others automatically). |
| `create_audio_mixer` | A | `AudioTools.cs` | Creates a new `.mixer` asset with its default Master group only, via reflection on `AudioMixerController` (see above). |
| `set_mixer_parameter` | A | `AudioTools.cs` | Sets an exposed `AudioMixer` float parameter via `SetFloat`; fails cleanly if the parameter isn't exposed to scripting yet. |
| `add_reverb_zone` | A | `AudioTools.cs` | Adds an `AudioReverbZone` (distance range + built-in preset, e.g. Cave/Hallway/Underwater). |
| `play_sound` | A, Fast | `AudioTools.cs` | Plays a one-shot clip for verification, adding an `AudioSource` if missing; returns the real `clipLength`. |
| `add_audio_occlusion` | C | `workflows.py` | Attaches/tunes `MCPAudioOcclusion`: raycasts to the listener each frame, applying a low-pass filter (explicitly added `AudioLowPassFilter`, not left to the script's own `Awake()`) when something's between source and listener. |
| `add_ambient_bed` | C | `workflows.py` | Sets up a looping 2D ambient `AudioSource`, optionally fading in over `fadeInDuration` seconds via a scaffolded `MCPAmbientFade` instead of starting at full volume. |
| `add_scare_stinger` | C | `workflows.py` | Attaches/tunes `MCPScareStinger` for jumpscares: call its public `Trigger()` to play a stinger clip and duck an `AudioMixer` parameter for `duckDuration` seconds, then restore it. |
| `add_footstep_audio_set` | C | `workflows.py` | Attaches `MCPSurfaceFootsteps`: each `surfaceClips` entry becomes a child `MCPSurfaceClip` GameObject (tag + clip); raycasts down each step and matches the ground collider's tag, falling back to `fallbackClipAssetPath`. |
| `add_dynamic_music` | C | `workflows.py` | Attaches `MCPDynamicMusic`: each `layerClipPaths` entry becomes a child silent looping `AudioSource` (auto-discovered, ordered calmest to most tense); `SetTension(0-1)` crossfades between layers. |

**Manual Test**: `MCPAudioOcclusion`/`MCPSurfaceFootsteps`/`MCPDynamicMusic`/`MCPAmbientFade`/`MCPScareStinger`'s
`Update()`-loop behavior (occlusion raycasting, footstep surface matching, music layer crossfading, fade-in,
stinger ducking/restore) all depend on `Awake()` having populated fields (`_surfaceClips`, `_layers`,
`_audioSource`), which isn't guaranteed immediately after `AddComponent()` outside Play mode -- structure, fields,
and wiring were all verified for real. All 8 atomic tools were verified end-to-end against a real Unity Editor,
including a real loadable `.mixer` asset with a findable Master group and a real `SetFloat` failure against an
unexposed parameter.

## `rendering` — URP post-processing **[GENRE]**

Scoped entirely to URP: live spikes confirmed core SRP's `Volume`/`VolumeProfile` types work without any specific
pipeline installed, but every actual effect override (Vignette, Bloom, DepthOfField, etc.) lives in
`UnityEngine.Rendering.Universal.*` -- HDRP has its own, differently-shaped equivalents not supported here. Every
URP type is resolved via reflection (same pattern as Cinemachine/Shader Graph/AudioMixer), so the bridge still
compiles in Built-in-only projects and fails clearly if URP isn't installed. A key API discovery: every
VolumeComponent override field (e.g. `Vignette.intensity`) is a `VolumeParameter<T>`-derived object, not a plain
value -- setting it means reflecting into a nested `value`/`overrideState` property pair on that object, confirmed
via live spike (and separately, that `Volume.priority`/`weight`/`blendDistance` are plain fields while
`isGlobal`/`profile` are properties on the very same component -- caught by a real `NullReferenceException` from
guessing `GetProperty` for all five). `toggle_ssao` adds a `ScreenSpaceAmbientOcclusion` renderer feature to a URP
Renderer Data asset using the exact `SerializedObject`/`m_RendererFeatures` array manipulation URP's own inspector
uses internally (read from `ScriptableRendererDataEditor.cs`'s source) -- a real, stable mechanism, not a fragile
internal-view-state hack like `create_audio_mixer`'s group creation.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `get_render_pipeline` | A | `RenderingTools.cs` | Reports the active pipeline: BuiltIn, Universal, HighDefinition, or Custom. |
| `create_post_process_volume` | A | `RenderingTools.cs` | Creates a new GameObject with a global or local `Volume` component. |
| `set_volume_profile` | A | `RenderingTools.cs` | Assigns an existing `VolumeProfile` asset to a `Volume`, optionally creating a blank one first. |
| `add_vignette` | A | `RenderingTools.cs` | Adds/tunes a Vignette override: darkened edges for claustrophobic framing. |
| `add_bloom` | A | `RenderingTools.cs` | Adds/tunes a Bloom override: light bleed and dread glow. |
| `add_depth_of_field` | A | `RenderingTools.cs` | Adds/tunes a Depth of Field override (Gaussian or Bokeh mode). |
| `add_chromatic_aberration` | A | `RenderingTools.cs` | Adds/tunes a Chromatic Aberration override: lens fringing for unease. |
| `add_motion_blur` | A | `RenderingTools.cs` | Adds/tunes a Motion Blur override (CameraOnly or CameraAndObjects mode). |
| `add_lens_distortion` | A | `RenderingTools.cs` | Adds/tunes a Lens Distortion override: screen warp for disorientation stingers. |
| `add_film_grain` | A | `RenderingTools.cs` | Adds/tunes a Film Grain override: gritty found-footage look. |
| `add_color_grading` | A | `RenderingTools.cs` | Adds/tunes ColorAdjustments (always) plus WhiteBalance/Tonemapping (only if their params are given) for a sickly palette. |
| `set_camera_clear_and_fog` | A | `RenderingTools.cs` | Ties a Camera's clear flags/background color to `RenderSettings.fogColor` for a seamless horizon. |
| `toggle_ssao` | A | `RenderingTools.cs` | Adds (if missing)/enables/disables a Screen Space Ambient Occlusion renderer feature on a URP Renderer Data asset. |

**Manual Test**: none -- every tool here was verified end-to-end against a real Unity Editor with URP installed:
real `Volume`/`VolumeProfile` assets, every override's fields read back correctly from the saved profile asset,
a real Camera synced to `RenderSettings.fogColor`, and a real SSAO renderer feature added/toggled and re-loaded
from disk without duplication.

## `vfx` — particles, decals, fog pockets, trails **[GENRE]**

`ParticleSystem`/`TrailRenderer` are core Unity (no reflection needed); `add_decal` (URP `DecalProjector`) and
`create_vfx_graph` (Visual Effect Graph package) are optional-package types resolved via reflection like the rest
of `rendering`. `create_vfx_graph` uses the real Editor API `UnityEditor.VisualEffectAssetEditorUtility.
CreateNewAsset(path)` (found by scanning the VFX Graph editor assembly for candidate methods, confirmed via live
spike to produce a genuinely loadable `VisualEffectAsset`) -- a much more robust mechanism than Shader Graph's
raw-JSON-template approach, since it's a real high-level asset-creation API rather than reverse-engineered file
format. `create_fog_volume` is a deliberate, documented scope call: this URP version (17.0.4) has no native "Local
Volumetric Fog" volume type (an HDRP/newer-URP-only feature, confirmed absent by searching the installed
package's source), so it's built instead from a real, working soft-particle cloud technique using Unity's
built-in alpha-blended particle material.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_particle_system` | A | `VfxTools.cs` | Creates a new GameObject with a configured `ParticleSystem` (main/shape/emission). |
| `set_particle_module` | A | `VfxTools.cs` | Edits Emission/Shape/ColorOverLifetime/Noise modules on an existing `ParticleSystem`. |
| `play_particle_system` | A **[LOOP]**, Fast | `VfxTools.cs` | Plays/stops/pauses/clears a `ParticleSystem`, for verification. |
| `create_vfx_graph` | A | `VfxTools.cs` | Creates a new blank VFX Graph asset via the real Editor API. Requires the Visual Effect Graph package. |
| `add_decal` | A | `VfxTools.cs` | Adds/tunes a URP `DecalProjector` on an existing GameObject (blood, grime, cracks). |
| `create_fog_volume` | A | `VfxTools.cs` | Creates a local fog pocket as a soft-particle cloud (see scope note above -- no native URP volumetric fog in this version). |
| `create_trail` | A | `VfxTools.cs` | Adds/tunes a `TrailRenderer` on an existing GameObject, for projectiles/entities. |
| `add_dust_motes` | C | `workflows.py` | Tuned preset: a sparse, slow-drifting World-space particle system with Noise-module drift, for stale still air. |
| `add_blood_splatter` | C | `workflows.py` | Places a one-off blood effect at a world position: an optional decal plus a fast particle spray approximating a burst. |
| `add_breath_fog` | C | `workflows.py` | Adds a periodic cold-breath puff: a zero-continuous-rate particle system plus a scaffolded `MCPBreathFog` that calls `Emit()` on a timer. |

**Manual Test**: none -- every atomic tool was verified end-to-end against a real Unity Editor, including a real
loadable `VisualEffectAsset`, a real `DecalProjector` with fields applied, and a real `TrailRenderer` (its
start/end color read back with a small quantization from Unity's internal `Color32` gradient backing, a real
precision limit rather than a tool bug).

## `animation` — Animator Controllers, blend trees, avatar masks, IK **[GENRE]**

Every tool except `add_ik_constraint` uses core Mecanim APIs (`UnityEditor.Animations.*`) directly -- no optional
package, no reflection, unlike most other GENRE groups. Two real gotchas surfaced by live spike, not guessed: (1)
`AnimatorController.parameters` (and similarly-shaped array properties) return a fresh deserialized copy on every
read -- mutating a previously-fetched element and reassigning `x.parameters = x.parameters` is a silent no-op; the
one already-fetched array's own element must be mutated and that same array instance written back. (2)
`Animator.Play()` + `Animator.Update(0)` really does change `GetCurrentAnimatorStateInfo()` outside Play Mode, so
`play_animation`'s state transition is verifiable without an actual Play Mode session -- unlike most
`Awake()`-dependent behavior elsewhere in this catalog. `add_ik_constraint` uses the optional Animation Rigging
package (`com.unity.animation.rigging`) via reflection, the same pattern as Cinemachine/URP; confirmed via live
spike that `TwoBoneIKConstraint`/`MultiAimConstraint`'s data (root/mid/tip/target/hint, sourceObjects, etc.) lives
behind a protected `m_Data` **field** on the generic `RigConstraint<,,>` base class, not the public `data`
property (a ref-return that throws `NotSupportedException` via reflection `Invoke`) -- and that
`WeightedTransform.transform` is a plain field too, not a property, the same field-vs-property trap
`RenderingTools.cs` hit with `Volume.priority`/`weight`/`blendDistance` in batch 14.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_animator_controller` | A | `AnimationTools.cs` | Creates a new, empty Animator Controller asset. |
| `add_animator_state` | A | `AnimationTools.cs` | Adds a state to a layer, optionally with a clip motion and/or as the layer's default state. |
| `add_animator_transition` | A | `AnimationTools.cs` | Adds a condition-based transition between two states (or from 'Any State'). |
| `add_animator_parameter` | A | `AnimationTools.cs` | Adds a Float/Int/Bool/Trigger parameter, optionally with a default value. |
| `create_blend_tree` | A | `AnimationTools.cs` | Creates a 1D or 2D BlendTree as a new state, for locomotion blending. |
| `assign_animator` | A | `AnimationTools.cs` | Attaches an Animator with a controller (and optional avatar/root motion setting) to a GameObject. |
| `play_animation` | A **[LOOP]**, Fast | `AnimationTools.cs` | Plays a state and evaluates one frame immediately, for verification -- works outside Play Mode (see gotcha #2 above). |
| `list_animation_clips` | A | `AnimationTools.cs` | Lists AnimationClip assets under a folder along with each clip's AnimationEvents. |
| `add_animation_event` | A | `AnimationTools.cs` | Adds a method-call event at a given time to an AnimationClip (footstep/hit frames). |
| `configure_avatar_mask` | A | `AnimationTools.cs` | Creates/edits an AvatarMask's humanoid body-part toggles (e.g. mask out legs for an upper-body-only aim layer). |
| `set_root_motion` | A | `AnimationTools.cs` | Enables/disables root motion on an existing Animator. |
| `add_ik_constraint` | A | `AnimationTools.cs` | Adds a TwoBoneIK (hand/foot) or Look (head/eye aim) constraint via Animation Rigging, auto-creating a shared Rig + RigBuilder on the animator root. Requires `com.unity.animation.rigging`. |

**Manual Test**: none -- every tool was verified end-to-end against a real Unity Editor with Animation Rigging
installed: real states/transitions/conditions/parameters (including the array-copy gotcha above), a real 2-child
BlendTree, a real Animator with a real assigned controller actually transitioning state outside Play Mode, a real
AnimationEvent round-tripped through a saved clip, a real AvatarMask with real body-part toggles, and real
TwoBoneIKConstraint/MultiAimConstraint components with real wired Transforms -- including confirming
`add_ik_constraint` reuses one auto-created Rig/RigLayer across multiple calls on the same animator root rather
than duplicating it.

## `gameplay` — ScriptableObject data/events, save/load, interactables, doors/keys, checkpoints, objectives

`create_scriptable_object` (already in `assets`) and `create_health_system` (already covered by `weapons`'
`create_damage_receiver` -- `MCPHealth` + optional `MCPHitZone`) are treated as already covered, the same dedup
`enemy_ai`'s batch applied to `scaffold_behavior_tree`/`add_bt_node`/`connect_bt_nodes`. `create_key_lock_pair`
is the one composite here that surfaced a real, previously-shipped bug: it needs the door to receive whichever
real `keyId` the key raises, which requires `wire_unity_event`'s **dynamic** forwarding mode -- and building it
is what triggered discovering that `wire_unity_event`'s original (batch 15) implementation only ever baked a
fixed constant argument, silently never forwarding the real value, since `create_interaction_prompt` never
happened to exercise a case where that distinction was observable. Every "attach once" scaffolded script here
(`MCPSaveData`, `MCPSaveSystem`, `MCPGameManager`, `MCPInventory`, `MCPInteractable`, `MCPDoor`, `MCPKeyItem`,
`MCPCheckpoint`, `MCPObjectiveSystem`, `MCPObjectiveListUI`) is marked `[DisallowMultipleComponent]` so a
composite that ensures a component exists on a possibly-already-configured GameObject (e.g. `create_key_lock_pair`
attaching `MCPDoor` to a door `create_door` might have already set up) is a safe no-op rather than silently
stacking a duplicate -- confirmed via live spike that `Undo.AddComponent` returns `null` (not an exception, not
a duplicate) against a type marked that way.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `set_scriptable_object_values` | A | `GameplayTools.cs` | Sets multiple fields on a ScriptableObject asset in one call -- the SO-asset equivalent of `set_component_properties_batch` (reuses the same `MCPComponentReflection` field/property logic, generic over any `object`, not GameObject-specific). |
| `wire_event_listener` | A | `GameplayTools.cs` | `wire_unity_event`'s sibling for when the event lives on a project asset (an SO event channel) instead of a scene GameObject. Shares the exact same dynamic/static/void-fallback logic via the new internal `MCPUnityEventWiring` helper. |
| `save_game_state` | A **[LOOP]**, Fast | `GameplayTools.cs` | Writes a JSON string to a named save slot under `Application.persistentDataPath` -- a generic round-trip verification primitive, independent of `create_save_system`'s scaffolded shape. |
| `load_game_state` | A **[LOOP]**, Fast | `GameplayTools.cs` | Reads back the JSON string previously written to a save slot via `save_game_state`. |
| `define_scriptable_object_type` | C | `workflows.py` | Generates a new ScriptableObject class with typed fields (float/int/string/bool/vector2/vector3/color), optionally with a `[CreateAssetMenu]` attribute. |
| `create_event_channel` | C | `workflows.py` | Scaffolds an SO-based event channel class (Void/Float/Int/String/Bool payload) with `Raise()`/`Raise(value)`, and creates an asset instance. |
| `create_save_system` | C | `workflows.py` | Scaffolds `MCPSaveData` (per-GameObject JSON blackboard) + `MCPSaveSystem` (`SaveSlot`/`LoadSlot` -- gathers every `MCPSaveData` in the scene into one real JSON file, using only `JsonUtility` so no Newtonsoft dependency is needed in the target project). |
| `create_game_manager` | C | `workflows.py` | Scaffolds `MCPGameManager`: a central state string (MainMenu/Playing/Paused/GameOver, or custom) with `SetState()` firing `onStateChanged`. |
| `create_inventory_system` | C | `workflows.py` | Attaches `MCPInventory`: a JSON blackboard (item id -> count), the same `get_component_field`/`set_component_field` pattern as `set_blackboard_key`. |
| `create_interactable` | C | `workflows.py` | Attaches a generic `MCPInteractable` (implements the shared `IInteractable` interface `fps_controller`'s raycaster already looks for) for levers/pickups/anything with a simple prompt+event shape. |
| `create_door` | C | `workflows.py` | Creates a placeholder-Cube GameObject with a scaffolded `MCPDoor`: rotates open/closed on `Interact()`, blocked while `isLocked`. |
| `create_key_lock_pair` | C | `workflows.py` | Creates a pickup key (`MCPKeyItem`) and really wires its `onPickup(keyId)` to a door's `MCPDoor.Unlock(keyId)` via `wire_unity_event`'s dynamic mode. |
| `create_checkpoint` | C | `workflows.py` | A trigger volume (via `add_trigger_volume`) plus a scaffolded `MCPCheckpoint` recording its own position; `Respawn(target)` moves a target there (temporarily disabling its `CharacterController`). `onTriggerEnter` wired to `Activate()` via the static-listener fallback. |
| `create_objective_system` | C | `workflows.py` | Attaches `MCPObjectiveSystem` (JSON blackboard + `onObjectiveCompleted`); optionally creates and really wires a Text list UI hook via `wire_unity_event`. |

**Manual Test**: `MCPKeyItem.Interact()`'s self-destruction via `Destroy(gameObject)` -- confirmed for real that
`Object.Destroy()` is a documented no-op outside Play Mode (Unity's own "Destroy may not be called from edit
mode" behavior), so only the real `onPickup` event firing with the real `keyId` could be verified headlessly;
the actual removal needs a Play Mode session, the same category as other Awake()-timing gotchas in this catalog.
Everything else was verified end-to-end against a real Unity Editor: real ScriptableObject field writes, a real
SO event channel with a real persistent listener (both static-baked and dynamic-forwarding modes), a real
`UnityEvent<Collider>` wired to a parameterless method via the static-listener fallback, a real save/load file
round trip through `Application.persistentDataPath`, real `MCPGameManager`/`MCPDoor`/`MCPCheckpoint` state
transitions, and a real `MCPSaveSystem.SaveSlot`/`LoadSlot` round trip that survived an in-between mutation.

## `scripting` — C# script CRUD + compile status

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_script` | A | `ScriptingTools.cs` | Creates a new C# script from a boilerplate template (MonoBehaviour/PlainClass/ScriptableObject). Triggers a domain reload. |
| `read_script` | A | `ScriptingTools.cs` | Reads the full text content of an existing C# script. |
| `update_script` | A | `ScriptingTools.cs` | Overwrites the full contents of an existing C# script. Triggers a domain reload. |
| `delete_script` | A | `ScriptingTools.cs` | Deletes a C# script and its `.meta` file. Destructive — requires `confirm: true`. |
| `list_scripts` | A | `ScriptingTools.cs` | Lists C# script paths under an optional subfolder filter. |
| `get_compile_status` | A | `ScriptingTools.cs` | Whether the Editor is currently compiling, plus structured errors/warnings from the most recent compile. |
| `get_compilation_errors` | A **[LOOP]** | `CompilationTools.cs` | Reads C# errors/warnings directly from `CompilationPipeline`, unaffected by Console clears/overflow. |
| `wait_for_compile` | A **[LOOP]**, Slow | `CompilationTools.cs` | Blocks until an in-progress compile/domain reload finishes, up to a timeout. |
| `resolve_type` | A | `ScriptingTools.cs` | Resolves a type name to its full name, assembly, and base type — disambiguate before add_component/set_component_field. |
| `list_assembly_definitions` | A | `AssemblyDefinitionTools.cs` | Lists `.asmdef` files under Assets/ with their name and references. |
| `create_assembly_definition` | A, Slow | `AssemblyDefinitionTools.cs` | Creates a new `.asmdef` scoping a folder into its own compiled assembly. Triggers a domain reload. |
| `update_assembly_definition` | A, Slow | `AssemblyDefinitionTools.cs` | Edits an existing `.asmdef`'s references/name/allowUnsafeCode; other fields untouched. |

## `physics` — colliders, Rigidbody, forces, raycasting

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `add_collider` | A | `PhysicsTools.cs` | Adds a Box/Sphere/Capsule collider to a GameObject. |
| `configure_rigidbody` | A | `PhysicsTools.cs` | Adds a Rigidbody if missing, then sets mass/drag/constraints/etc. |
| `set_velocity` | A | `PhysicsTools.cs` | Sets linear and/or angular velocity on a Rigidbody. |
| `apply_force` | A | `PhysicsTools.cs` | Applies a force or impulse to a Rigidbody. |
| `get_rigidbody_state` | A | `PhysicsTools.cs` | Reads back a Rigidbody's mass/drag/velocity/etc. |
| `raycast` | A **[LOOP]** | `PhysicsTools.cs` | Casts a ray and returns the first hit, if any. |
| `spherecast` | A **[LOOP]** | `PhysicsTools.cs` | Casts a sphere along a direction and returns the first hit, if any (wider-tolerance raycast). |
| `overlap_query` | A **[LOOP]** | `PhysicsTools.cs` | Returns every collider overlapping a Sphere or Box volume. |
| `add_joint` | A | `PhysicsTools.cs` | Adds a Hinge/Fixed/Spring/Configurable joint, auto-adding a Rigidbody if missing (Unity's own requirement). |
| `set_layer_collision_matrix` | A | `PhysicsTools.cs` | Sets whether two physics layers collide with each other, project-wide. |
| `create_physics_material` | A | `PhysicsTools.cs` | Creates a `PhysicsMaterial` asset with friction/bounciness settings. Normalizes the asset extension to `.physicMaterial` — the asset importer only recognizes the legacy spelling even though the runtime class was renamed `PhysicMaterial` → `PhysicsMaterial`. |
| `configure_physics_settings` | A | `PhysicsTools.cs` | Configures project-wide gravity and solver iteration counts; omitted params left unchanged. |
| `add_trigger_volume` | C | `workflows.py` | Creates a trigger collider (Box/Sphere) and attaches a scaffolded `MCPTriggerRelay` component exposing `onTriggerEnter`/`onTriggerExit` UnityEvents. |

## `assets` — prefabs, materials, ScriptableObjects, generic asset ops

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_prefab` | A | `AssetTools.cs` | Saves an existing GameObject as a new prefab asset. |
| `instantiate_prefab` | A | `AssetTools.cs` | Instantiates a prefab asset into the active scene. |
| `create_material` | A | `AssetTools.cs` | Creates a new Material asset with a given shader and optional color. |
| `set_material_color` | A | `AssetTools.cs` | Sets the main color (incl. alpha) on an existing Material. |
| `create_scriptable_object` | A | `AssetTools.cs` | Creates a new ScriptableObject asset instance of a given (already-compiled) class. |
| `list_assets` | A | `AssetTools.cs` | Lists asset paths filtered by extension and/or subfolder. |
| `delete_asset` | A | `AssetTools.cs` | Deletes any asset via AssetDatabase. Destructive — requires `confirm: true`. |
| `create_prefab_variant` | A, Slow | `PrefabTools.cs` | Creates a Prefab Variant (inherits from a base, stores only the differences). |
| `open_prefab_mode` | A | `PrefabTools.cs` | Opens a prefab asset in isolated Prefab Mode for editing. |
| `close_prefab_mode` | A | `PrefabTools.cs` | Exits Prefab Mode, saving changes back to the prefab asset by default. |
| `apply_prefab_overrides` | A, destructive | `PrefabTools.cs` | Applies an instance's overrides back to the source prefab (affects every other instance too). |
| `revert_prefab_overrides` | A, destructive | `PrefabTools.cs` | Reverts an instance's overrides to the source prefab's defaults (including a custom instance name — that's an override too). |
| `get_prefab_overrides` | A | `PrefabTools.cs` | Lists an instance's added/removed components, added GameObjects, modified objects, and source prefab path. |
| `unpack_prefab` | A, destructive | `PrefabTools.cs` | Disconnects an instance from its source prefab (outermost level, or `completely` for every nested prefab too). |
| `import_asset` | A, Slow | `AssetTools.cs` | Copies an external file into the project under Assets/ and imports it. |
| `move_asset` | A | `AssetTools.cs` | Moves/renames an asset via AssetDatabase, preserving its GUID. |
| `get_asset_dependencies` | A | `AssetTools.cs` | Lists what an asset references, and optionally what references it (slower). |
| `reimport_asset` | A, Slow | `AssetTools.cs` | Forces a reimport with current importer settings. |
| `set_texture_import_settings` | A, Slow | `AssetTools.cs` | Configures texture type/compression/mipmaps/sRGB/max size. |
| `set_model_import_settings` | A, Slow | `AssetTools.cs` | Configures model animation import/type, material import mode, global scale. |
| `create_folder` | A | `AssetTools.cs` | Creates a folder under Assets/, including any missing parent folders. |
| `mark_addressable` | A | `AssetTools.cs` | Marks an asset Addressable via reflection (optional package, not a hard dependency). **Manual Test**: needs a project with Addressables actually installed. |
| `create_asset_bundle` | A, Slow | `AssetTools.cs` | Assigns assets to a named bundle and builds all bundles for the current target. |
| `replace_prefab_instances` | C | `workflows.py` | Finds every instance of one prefab in the scene and replaces each with another, preserving transform/parent/name. |
| `set_material_properties` | A | `MaterialTools.cs` | Sets exactly one of: a shader color property, a float property, a texture, or a shader keyword on a Material. |
| `assign_material` | A | `MaterialTools.cs` | Assigns a Material asset to a GameObject's Renderer, by element index. |
| `get_material_properties` | A | `MaterialTools.cs` | Reads a Material's shader name, render queue, enabled keywords, and declared shader properties. |
| `list_shaders` | A | `MaterialTools.cs` | Lists shader names known to the project, optionally filtered by substring. |
| `create_shader_graph` | A, Slow | `MaterialTools.cs` | Creates a blank Unlit Shader Graph asset. **Manual Test**: requires the Shader Graph package (`com.unity.shadergraph`) — not installed in the verification project, only its clean-failure path was confirmed. |
| `inspect_shader_graph` | A | `MaterialTools.cs` | Reads a Shader Graph asset's exposed properties via reflection. **Manual Test**: same package requirement as `create_shader_graph`. |
| `set_render_queue` | A | `MaterialTools.cs` | Overrides a Material's render queue (or resets to shader default). |
| `create_material_variant` | A | `MaterialTools.cs` | Copies a Material into a new independent asset (a real duplicate, not a Unity "Material Variant" — that concept doesn't exist for materials the way it does for prefabs). |
| `set_global_shader_property` | A | `MaterialTools.cs` | Sets a global shader property (color/float/vector) visible to every shader via `Shader.SetGlobal*`. |
| `create_render_texture` | A | `AssetTools.cs` | Creates a RenderTexture asset, for camera-to-texture setups (CCTV/monitor props, minimaps, portals). |

## `ui` — UGUI Canvas, elements, layout, RectTransform, HUD widgets

The source catalog's `add_ui_text`/`add_ui_image`/`add_ui_button`/`add_layout_group` are treated as already
covered by the existing `create_ui_element` (Panel/Button/Text/Image/InputField in one tool) and `set_layout` --
the same kind of dedup `enemy_ai`'s batch applied to `scaffold_behavior_tree`/`add_bt_node`/`connect_bt_nodes`.
`create_interaction_prompt` and `create_pause_menu` are the two composites here that need a *real* runtime wire,
not just decoration -- both use the new core-group `wire_unity_event` tool (added this batch) to hook a real
UnityEvent (`MCPInteractionRaycaster.onInteractableFound`/`onInteractableLost`, `Button.onClick`) to a method on
the scaffolded UI script, rather than leaving the binding as a manual follow-up.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_canvas` | A | `UITools.cs` | Creates a Canvas + CanvasScaler + GraphicRaycaster, plus an EventSystem if missing. |
| `create_ui_element` | A | `UITools.cs` | Creates a Panel/Button/Text/Image/InputField under a parent path. |
| `set_rect_transform` | A | `UITools.cs` | Sets anchorMin/Max/pivot/anchoredPosition/sizeDelta on a UI element. |
| `get_rect_transform` | A | `UITools.cs` | Reads back a UI element's RectTransform values. |
| `set_layout` | A | `UITools.cs` | Adds/reconfigures a Horizontal/Vertical/Grid layout group. |
| `set_ui_color` | A | `UITools.cs` | Sets color (incl. alpha) on an Image or Text/TMP Graphic. |
| `create_health_bar` | C | `workflows.py` | Background panel + Filled/Horizontal fill Image, driven by a scaffolded `MCPValueBarUI.SetValue(current, max)` -- not wired to `MCPHealth` directly, to avoid a hard dependency on the `weapons` group. |
| `create_ammo_counter` | C | `workflows.py` | A Text readout driven by a scaffolded `MCPAmmoCounterUI.SetAmmo(current, reserve)`. |
| `create_crosshair` | C | `workflows.py` | A single-dot reticle Image driven by a scaffolded `MCPCrosshairUI` that grows with its public `spread` (0-1) via `SetSpread()`. |
| `create_interaction_prompt` | C | `workflows.py` | A hidden "Press E"-style Text prompt, scaffolded as `MCPInteractionPromptUI`; if `raycasterPath` is given, really wires `MCPInteractionRaycaster`'s `onInteractableFound`/`onInteractableLost` to `Show`/`Hide` via `wire_unity_event`. |
| `create_pause_menu` | C | `workflows.py` | A hidden Vertical-layout panel with Resume/Quit buttons, scaffolded as `MCPPauseMenuUI` (also drives `Time.timeScale`); button clicks really wired via `wire_unity_event`. |
| `create_subtitle_system` | C | `workflows.py` | A hidden Text element driven by a scaffolded `MCPSubtitleUI.ShowLine(text)` that auto-hides after `displayDuration` seconds. |

## `behavior_tree` — composite Behavior Tree scaffolding

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `scaffold_behavior_tree_framework` | C | `workflows.py` | Generates the core BT runtime scripts (BTNode, Sequence, Selector, ActionNode, BTRunner) if missing. |
| `create_behavior_tree` | C | `workflows.py` | Builds a complete tree in-scene from a nested spec (root + composite + descendant nodes). |
| `add_behavior_tree_node` | C | `workflows.py` | Adds a node under an existing tree node by path, without rebuilding the whole tree. |
| `set_blackboard_key` | C | `workflows.py` | Sets a key on a GameObject's `MCPBlackboard` (a JSON string field), for custom nodes/sensors to read. Listed as atomic in the source catalog, but ships as a composite: a Blackboard has to be a scaffolded user-project script like every other gameplay script this server generates, and the bridge's compiled C# can't reference a type that only exists in the target project. |

## `inspection` — the agent's eyes **[LOOP]**

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `capture_scene_view` | A **[LOOP]** | `InspectionTools.cs` | Renders the active Scene view camera to PNG, returned inline as base64. |
| `capture_game_view` | A **[LOOP]** | `InspectionTools.cs` | Renders the primary game camera to PNG (Edit or Play mode). |
| `capture_from_camera` | A **[LOOP]** | `InspectionTools.cs` | Renders a specific named camera (by path) to PNG, regardless of main/game camera status. |
| `read_console_log` | A **[LOOP]** | `ConsoleTools.cs` | Reads cached console messages since the last domain reload. |
| `clear_console_log` | A | `ConsoleTools.cs` | Clears the Console window and this tool's cached buffer. |
| `draw_debug_gizmo` | A **[LOOP]** | `InspectionTools.cs` | Draws a temporary Line/Ray/wireframe Box via `Debug.DrawLine`/`DrawRay`, visible live in the Scene/Game view. Not included in camera-render screenshots (gizmos aren't part of `Camera.Render()`). **Manual Test**: visual confirmation needs a real, visible Editor. |
| `get_frame_debugger_info` | A **[LOOP]** | `InspectionTools.cs` | Best-effort per-event render breakdown via reflection over the internal Frame Debugger API (the type is Unity-internal; only its public members are reflectable). Returns an empty list gracefully if unavailable/headless. **Manual Test**: needs a real rendering frame to return non-empty results. |
| `capture_editor_window` | A **[LOOP]** | `InspectionTools.cs` | Screenshots any open Editor window by title via `InternalEditorUtility.ReadScreenPixel` (real screen pixels, unlike the camera-based captures — includes UI/Overlay canvases and window chrome). **Manual Test**: requires the Editor to be visible on a real display; cannot be verified headless. |
| `get_object_screen_bounds` | A **[LOOP]** | `InspectionTools.cs` | Projects a GameObject's Renderer bounds into screen space via a given/main camera — min/max pixel rect, for aim/HUD verification. |

## `testing` — Play mode control + automated tests **[LOOP — the autonomy backbone]**

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `enter_play_mode` | A **[LOOP]**, Slow | `PlayModeTools.cs` | Starts Play mode and waits for the transition (incl. domain reload) to settle. |
| `exit_play_mode` | A **[LOOP]**, Slow | `PlayModeTools.cs` | Stops Play mode and waits for the transition back to settle. |
| `pause_play_mode` | A **[LOOP]** | `PlayModeTools.cs` | Pauses/resumes Play mode; fails clearly if not currently playing. |
| `list_tests` | A | `TestRunnerTools.cs` | Lists available EditMode/PlayMode tests without running them. |
| `run_edit_mode_tests` | A **[LOOP]**, Slow | `TestRunnerTools.cs` | Runs EditMode tests (optionally filtered), returns pass/fail + failure details. |
| `run_play_mode_tests` | A **[LOOP]**, Slow | `TestRunnerTools.cs` | Runs PlayMode tests (optionally filtered), entering Play mode as needed. |
| `assert_scene_state` | A **[LOOP]** | `AssertionTools.cs` | Asserts a GameObject exists / has a component / a field equals an expected value. Clear pass/fail, no throw. |

## `terrain` — terrain creation, sculpting, texture/detail/tree painting, holes, wind, prop scattering

All eight atomic tools share one `ApplyCircularBrush` helper: it converts a world-space
center+radius into grid cells with linear falloff, independently per X/Z axis, since a
`TerrainData`'s heightmap/alphamap/detail-map resolutions can all differ from the
terrain's world size and from each other. One real gotcha found via live spike: a
freshly-created `TerrainData`'s `detailResolution` is `0`, and any `GetDetailLayer`/
`SetDetailLayer` call throws `IndexOutOfRangeException` until `SetDetailResolution()` is
called first — `create_terrain` does this up front so `place_terrain_details` never hits
it. All heightmap/alphamap/detail arrays are indexed `[z, x]` (row = Z, column = X),
confirmed by raising terrain at a known world position and checking the array's middle
index landed where expected.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_terrain` | A | `TerrainTools.cs` | Creates a Terrain GameObject with a new `TerrainData` asset of given size/resolution. |
| `sculpt_terrain_height` | A | `TerrainTools.cs` | Raises/lowers/flattens heights within a circular brush, linear falloff at the edge. |
| `add_terrain_layer` | A | `TerrainTools.cs` | Creates/assigns a `TerrainLayer` asset (diffuse/normal textures, tiling) as a paintable layer. |
| `paint_terrain_texture` | A | `TerrainTools.cs` | Paints alphamap weight for one layer within a circular brush. |
| `place_terrain_trees` | A | `TerrainTools.cs` | Scatters `TreeInstance`s (from a `TerrainData` tree prototype) within a circular brush. |
| `place_terrain_details` | A | `TerrainTools.cs` | Paints detail-mesh/grass density within a circular brush. |
| `paint_terrain_holes` | A | `TerrainTools.cs` | Carves/fills terrain holes within a circular brush, for cave mouths and entrances. |
| `create_wind_zone` | A | `TerrainTools.cs` | Creates a `WindZone` (Directional or Spherical) for foliage motion. |
| `scatter_props` | C | `workflows.py` | Procedurally scatters prop prefab instances in a circular area via `instantiate_prefab` + `snap_to_ground`, with optional random Y rotation and a reproducible seed. |

## `timeline` — Timeline assets, tracks/clips, signals, track bindings, camera-cut, scare sequences

`PlayableDirector`/`Playable` are core Unity (`UnityEngine.DirectorModule`), referenced
directly; everything else (`TimelineAsset`, tracks, clips, `SignalEmitter`/`SignalAsset`)
lives in the optional `com.unity.timeline` package and is resolved via reflection, the
same pattern as Cinemachine/Animation Rigging. Two genuinely surprising discoveries from
live spikes: (1) Signal tracks are **Marker**-based, not clip-based — `SignalTrack :
MarkerTrack`, so emitters are created via `TrackAsset.CreateMarker<SignalEmitter>(time)`,
not `CreateClip<T>()` (neither `SignalAsset` nor `SignalEmitter` implement
`IPlayableAsset`, so `CreateClip<T>()` against them fails to compile). (2)
`CinemachineTrack`/`CinemachineShot` have **no namespace prefix** in the Cinemachine
assembly (`Type.GetType("CinemachineTrack, Cinemachine")` resolves; the `Cinemachine.`-
prefixed form does not) — confirmed via an assembly-scanning spike. `add_camera_cut_track`
wires each shot's camera via the same ExposedReference/`PropertyName`/
`PlayableDirector.SetReferenceValue` mechanism the Timeline Editor UI itself uses when you
drag a camera onto a shot clip. `play_timeline`'s `Evaluate()`-after-setting-`.time` really
re-samples outside Play Mode, confirmed via spike (same category as `Animator.Play()` +
`Update(0)`).

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_timeline` | A | `TimelineTools.cs` | Creates a new `TimelineAsset`, optionally attaching it to a `PlayableDirector`. |
| `add_timeline_track` | A | `TimelineTools.cs` | Adds an Animation/Audio/Activation/Signal track. |
| `add_timeline_clip` | A | `TimelineTools.cs` | Places a clip on an Animation/Audio/Activation track. |
| `add_timeline_signal` | A | `TimelineTools.cs` | Adds a `SignalEmitter` marker to a Signal track, optionally wiring a real `SignalReceiver` reaction via the same internal `MCPUnityEventWiring` helper `wire_unity_event` uses. |
| `bind_timeline_track` | A | `TimelineTools.cs` | Binds a track to a scene object via the `PlayableDirector`'s generic bindings. |
| `play_timeline` | A **[LOOP]** | `TimelineTools.cs` | Sets a `PlayableDirector`'s time and evaluates immediately, to verify a sequence without Play Mode. |
| `add_camera_cut_track` | A | `TimelineTools.cs` | Adds a Cinemachine track with camera-cut shots for cutscene coverage. |
| `create_scare_sequence` | C **[GENRE]** | `workflows.py` | Choreographs a scripted scare: an Activation track flickers a light for a duration, an optional Animation track plays a clip, and Signal tracks fire an audio stinger (`MCPScareStinger.Trigger`) and/or a camera shake (`Cinemachine.CinemachineImpulseSource.GenerateImpulse`) at precise moments. |

## `levelgen` — procedural layout, room/corridor generation, spawn points, LOD/lightmap/occlusion, streaming, NavMesh validation

`configure_lod_group`/`generate_lightmap_uvs`/`bake_occlusion_culling` are the only new
atomic tools here (plain `UnityEditor`/core APIs, no reflection); the other six are pure
Python compositions. `connect_rooms` implements the standard dungeon-graph
connector-snap technique in Python: given a fixed room's connector and a moving room's
connector, it rotates (Y-axis only — a flat, grid-based layout assumption) and translates
the moving room so its connector ends up exactly at the fixed connector's position,
facing the opposite direction, with no new Unity-side API needed beyond `get_transform`/
`set_transform`. `validate_level_navmesh` reuses `set_agent_destination`'s own documented
dual purpose (move an agent *and* test reachability via `pathStatus`) rather than adding
a new atomic tool.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `generate_grid_layout` | C | `workflows.py` | Lays out a rows x cols grid of blockout cells (or room prefab instances) under one parent. |
| `place_spawn_points` | C | `workflows.py` | Distributes player/enemy/item spawn markers in a circular area, with rejection sampling for a minimum spacing. |
| `carve_room` | C | `workflows.py` | Instantiates a room module prefab and reports its connector child objects (by name-prefix convention). |
| `connect_rooms` | C | `workflows.py` | Joins two room modules by rotating/translating the moving one so its connector meets the fixed one's, facing opposite. |
| `bake_occlusion_culling` | A **[LOOP]** | `LevelGenTools.cs` | Bakes occlusion culling data for the active scene. |
| `configure_lod_group` | A | `LevelGenTools.cs` | Sets up LOD levels (renderers + screen-relative-height thresholds) on a GameObject. |
| `generate_lightmap_uvs` | A | `LevelGenTools.cs` | Generates lightmap UVs (UV2) for a mesh via Unity's own unwrapping parameters. |
| `set_scene_streaming` | C | `workflows.py` | A trigger zone that additively loads/unloads a scene by name on enter/exit, via a scaffolded `MCPSceneStreamer`. |
| `validate_level_navmesh` | C **[LOOP]** | `workflows.py` | Confirms a set of key points are reachable on the baked NavMesh via a temporary `NavMeshAgent` + `set_agent_destination`'s `pathStatus`. |

## `input` — Input System action assets, maps/actions/bindings, contexts, InputReader, rebinding, synthetic input

Every type here is resolved via reflection (`com.unity.inputsystem` is optional, not
bundled). `.inputactions` assets are plain JSON under a custom `ScriptedImporter` —
`InputActionAsset.ToJson()`/`File.WriteAllText`/`AssetDatabase.ImportAsset` is a real,
stable public-API round trip, confirmed via spike. One real bug found the same way:
`ToJson()` throws `ArgumentNullException` against a genuinely blank
`ScriptableObject.CreateInstance<InputActionAsset>()` (its internal `m_ActionMaps` field
is `null`, not an empty array, until the asset has round-tripped through the real
importer once) — `create_input_action_asset` works around this by writing a minimal
hand-built JSON template directly for creation only; every later edit goes through the
normal `ToJson()` path once the asset is real. Another surprise: `InputActionAsset`/
`InputActionMap` have **no instance** `AddActionMap`/`AddAction` methods at all in this
Input System version — they're static extension methods on
`InputActionSetupExtensions`, found only after an instance-method lookup returned `null`
and threw on `Invoke()`. `simulate_input` uses `InputState.Change()` (the real API a
control's own setters use internally), which works for analog/axis controls but
explicitly **rejects bitfield/digital controls** (keyboard keys, mouse/gamepad buttons)
with `ArgumentException` — a genuine Input System constraint, not a bug here, so
`simulate_input` surfaces that clearly rather than silently no-op'ing or crashing.
`generate_input_reader` sidesteps Unity's C# code-generation feature entirely, scaffolding
a hand-written SO reader that hooks `InputActionMap.FindAction` directly at runtime.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `create_input_action_asset` | A | `InputTools.cs` | Creates a new, empty `.inputactions` asset. |
| `list_input_action_maps` | A | `InputTools.cs` | Lists action maps, actions, and bindings in an `.inputactions` asset. |
| `add_input_action` | A | `InputTools.cs` | Adds an action to a map, creating the map if it doesn't exist. |
| `add_input_binding` | A | `InputTools.cs` | Adds a control binding (keyboard/mouse/gamepad path) to an existing action. |
| `generate_input_reader` | C | `workflows.py` | Scaffolds a ScriptableObject-based InputReader: one C# event per action, hooked to the action map at runtime — no Unity code-generation step required. |
| `add_rebinding_ui` | C | `workflows.py` | Attaches a scaffolded `MCPRebindButton` to a UI Button: interactive rebind via `InputActionRebindingExtensions.PerformInteractiveRebinding`, persisted to `PlayerPrefs`. |
| `set_action_map_active` | A | `InputTools.cs` | Enables/disables an action map on a loaded instance (UI vs gameplay input contexts). |
| `simulate_input` | A **[LOOP]** | `InputTools.cs` | Injects a synthetic value via `InputState.Change()` + `InputSystem.Update()`, for automated play-testing of analog/axis controls. |

**Manual Test**: `simulate_input` against a digital/bitfield control (keyboard keys,
mouse/gamepad buttons) fails by design — see above; verify button-driven behavior in a
real Play Mode session instead. `add_rebinding_ui`'s interactive rebind needs a real
input device press to complete, which isn't observable in an automated/headless invoke
test.

---

## `profiling` — CPU/GPU/memory profiler frames, memory snapshots, render stats, performance analysis **[LOOP]**

Every type here (`ProfilerDriver`, `UnityStats`, `HierarchyFrameDataView`) is a real,
directly-compilable public API despite living in the "Internal"-named
`UnityEditorInternal` namespace — confirmed via live spike, no reflection needed, unlike
most of this codebase's optional-package integrations. Two real batchmode limitations,
both confirmed via spike rather than assumed: `ProfilerDriver`'s frame buffer is empty
(`lastFrameIndex == -1`) until a real Play Mode/Development Player session has actually
run frames with the Profiler enabled, and `UnityStats`' render counters read zero until
at least one real frame has rendered (Play Mode or a Game view repaint).

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `capture_profiler_frames` | A **[LOOP]** | `ProfilingTools.cs` | Enables the Profiler and reports the CPU/Memory/Rendering counter breakdown for the most recently captured frames. |
| `get_memory_snapshot` | A **[LOOP]** | `ProfilingTools.cs` | Total allocated/reserved/Mono memory plus the top N objects by runtime memory size, via `Profiler.GetRuntimeMemorySizeLong` over every loaded object — no optional Memory Profiler package needed. |
| `get_render_stats` | A **[LOOP]** | `ProfilingTools.cs` | Draw calls, batches, triangles, vertices, SetPass calls, shadow casters, texture memory, via `UnityStats`. |
| `analyze_performance` | C **[LOOP]** | `workflows.py` | Flags too many realtime lights/colliders (`get_scene_stats`, scene group) and, once a real frame has rendered, too many draw calls/SetPass calls (`get_render_stats`) — combines existing tools rather than adding new scene-querying capability. |

**Manual Test**: `capture_profiler_frames` and `get_render_stats` only return non-empty/
non-zero data once a real Play Mode session (or Development Player) has actually
rendered frames with the Profiler enabled — both report gracefully (empty frames / all
zero) rather than erroring in the meantime, confirmed via live spike.

## `build` — Player builds, build-relevant Player Settings, UPM packages, project settings

`build_player`'s `BuildPipeline.BuildPlayer()` call and its `BuildReport` contract
(`result`/`totalErrors`/`totalWarnings`/`totalTime`/`totalSize`/`outputPath`) were
confirmed via a real end-to-end build in the verify project — it genuinely failed (a
pre-existing ShaderGraph assembly-resolution issue in that scratch project, unrelated to
this tool), which was itself useful confirmation that failures come back as real,
structured `BuildReport` data rather than an uncaught exception. `manage_packages`'
`Client.List`/`Add`/`Remove`/`Search` all return async `*Request` objects — confirmed via
live spike that a bounded spin-wait (`Thread.Sleep` in a loop checking `IsCompleted`)
resolves correctly in well under a second for list/search (and completes a real
install/uninstall round trip in the verify project) without deadlocking the Editor's main
thread. `manage_project_settings`' tag/layer editing uses the well-known
`SerializedObject`-over-`ProjectSettings/TagManager.asset` technique (there's no other
public API for it), confirmed via spike that edits persist and are visible via
`InternalEditorUtility.tags`/`layers` afterward. A new `MCPPathGuard.TryResolveWithinProject`
was added alongside the existing `TryResolveWithinAssets` for `build_player`'s output
path, which legitimately needs to write outside `Assets/` but still must not escape the
project root.

| Tool | Type | Implementation | Description |
|---|---|---|---|
| `build_player` | A **[LOOP]**, Slow | `BuildTools.cs` | Builds a Player via `BuildPipeline.BuildPlayer`, using Build Settings' enabled scenes by default. Reports real success/failure with error/warning counts, build time, and output size. |
| `configure_build_settings` | A | `BuildTools.cs` | Company/product name, bundle version, and scripting backend (Mono2x/IL2CPP) for a build target group. Scenes-in-build stay with the existing `add_scene_to_build`/`list_scenes_in_build` (scene group) rather than being duplicated here. |
| `manage_packages` | A, Slow | `BuildTools.cs` | List/add/remove/search UPM packages via the real Package Manager `Client` API. `remove` requires `confirm: true`, checked manually since a single multi-action tool can't be conditionally destructive per-action at the attribute level. |
| `manage_project_settings` | A | `BuildTools.cs` | Tags, user layers (8-31; 0-7 are Unity's reserved built-in layers and are refused), `Time.fixedDeltaTime`/`maximumDeltaTime`, and quality level. Physics settings are already covered by `configure_physics_settings` (physics group) and aren't duplicated here. |

## Groups defined but not yet built out

Every group in `groups.py`'s `GROUP_CATALOG` now has at least one tool — the full
`unity-mcp-300-tools-fps-horror.md` catalog has been built out end to end across all 18
batches.

## Build progress toward the full catalog

Tracked batch-by-batch, roughly in the order that gets a playable/verifiable slice
fastest (loop tools and control spine first, then genre systems, then polish/ship —
see `unity-mcp-300-tools-fps-horror.md`'s own "How to prioritize" section).

| Batch | Scope | Status |
|---|---|---|
| 1 | Inspection group completion (`capture_from_camera`, `draw_debug_gizmo`, `get_frame_debugger_info`, `capture_editor_window`, `get_object_screen_bounds`) + Scripting group completion (`resolve_type`, `list/create/update_assembly_definition`) | **Done** |
| 2 | Editor Control & Session (extend `core`) — **`run_csharp` deliberately excluded**: arbitrary code execution would bypass every existing safety mechanism (confirm gate, path guard, rate limiter) at once; the user chose to skip it rather than add any variant of it | **Done** |
| 3 | Scene Management (new `scene` group) | **Done** |
| 4 | GameObject/Transform + Component extensions (`core`) | **Done** |
| 5 | Prefabs + Assets/Import extensions (`assets`) | **Done** |
| 6 | Materials/Shaders + Physics extensions | **Done** |
| 7 | Lighting (new `lighting`) — GENRE | **Done** |
| 8 | Cameras & Cinemachine (new `cameras`) | **Done** |
| 9 | NavMesh & Navigation (new `navmesh`) | **Done** |
| 10 | FPS Character Controller (new `fps_controller`) — GENRE | **Done** |
| 11 | Weapons & Combat (new `weapons`) — GENRE | **Done** |
| 12 | Enemy AI (extend `behavior_tree` + new `enemy_ai`) — GENRE | **Done** |
| 13 | Audio (new `audio`) — GENRE | **Done** |
| 14 | Rendering/Post-FX + VFX (new `rendering` + `vfx`) — GENRE | **Done** |
| 15 | UI/HUD extensions + Animation (extend `ui`, new `animation`) | **Done** |
| 16 | Gameplay Systems & Data (new `gameplay`) | **Done** |
| 17 | Terrain + Timeline + Level Gen + Input (new `terrain`, `timeline`, `levelgen`, `input`) | **Done** |
| 18 | Profiling + Build/Project/Packages (new `profiling`, `build` — ship) | **Done** |

A new C#-side infrastructure fix landed in Batch 1 that every later batch benefits
from: `MCPToolRegistry` previously had no support for `string[]`-typed parameters
at all (schema generation fell through to a bare `"string"` type, and argument
coercion would throw on a real JSON array). Fixed in `MCPToolRegistry.cs`
(`BuildSchema`/`ConvertArg`) — array parameters now emit a correct `{"type": "array",
"items": {"type": "string"}}` schema and coerce from the wire's `JArray` correctly.
