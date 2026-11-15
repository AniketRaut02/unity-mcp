"""
Tool group definitions and per-process active-group state.

One Python server process = one MCP client session (Claude Code/Codex each spawn
their own stdio subprocess per session), so a plain module-level set is genuinely
per-session state without needing anything fancier -- unlike a server built to
multiplex several concurrent sessions over one process.

Deliberately a visibility/prompt-economy mechanism, not a security boundary: a
tool hidden from list_tools() because its group isn't active can still be called
directly by name if a client already knows it exists (e.g. from an earlier
manage_tools list_groups call or from having seen it in a prior turn). Every tool
still goes through its own real safety mechanisms regardless of group state --
the destructive/confirm gate, path guard, and rate limiter from Phase 2 don't
care whether the calling group is "active". Grouping exists to keep the
default visible tool list focused (fewer tokens, better routing), matching how
most MCP tool-group implementations work.

Disabled groups (new, for the Unity-side "Tool Groups" window) are a genuinely
different, stronger mechanism than plain activation: a group listed in
Library/MCP/tool_groups_config.json's "disabledGroups" is treated as if it does
not exist at all -- excluded from GROUP_CATALOG-derived listings, refused by
name from activate(), and (see server.py/workflows.py) refused at call time too,
mimicking "unknown tool"/"unknown group" rather than a distinct "disabled" error,
so a client can't infer the group's existence even from the error message. This
file is written by the Unity Editor's Tool Groups window (a human control), not
by any MCP tool -- there is deliberately no way for the AI itself to disable or
re-enable a group, only a human with Editor access. "core" can never be disabled
(enforced both here and in the Unity-side writer, defense in depth).

The same config file's "defaultActiveGroups" seeds _active_groups for a brand
new process (and on reset()) -- but, per the project owner's own call when this
was designed, does NOT retroactively push into an already-running session: only
disabling is meant to take effect immediately for live sessions, since it's a
security-style hard gate, while routine activation is closer to ordinary session
state that a human toggling defaults shouldn't yank out from under a live
conversation the AI is already having. Re-read on a short TTL (not on every
single call) so a human's change is visible within about a second without
hitting disk constantly.
"""
import json
import os
import time
from pathlib import Path
from typing import Optional

from . import security

GROUP_CATALOG: dict[str, str] = {
    "core": "Essential scene, component, and query tools, plus batch_execute and manage_tools. Always active.",
    "scripting": "Create/read/update/delete C# scripts; check compile status.",
    "physics": "Colliders, Rigidbody configuration, forces, raycasting.",
    "assets": "Prefabs, materials, ScriptableObjects, and generic asset listing/deletion.",
    "ui": "UGUI Canvas, buttons, layout groups, RectTransform.",
    "behavior_tree": "Composite tools for scaffolding and building custom Behavior Trees.",
    "inspection": "Screenshots, console log reads, and compile diagnostics — lets an agent check its own work.",
    "testing": "Play mode control (enter/exit/pause) and automated test running.",
    "scene": "Multi-scene management: open/save/create/close scenes, scene hierarchy queries, build-settings scene list.",
    "lighting": "Lights, shadows, lightmapping/GI, ambient, reflection/light probes, skybox, fog.",
    "cameras": "Cameras, camera stacking, Cinemachine virtual cameras/body/aim, camera shake/impulse, render-texture cameras.",
    "navmesh": "NavMesh baking (scene-wide and local-volume), agents, obstacles, off-mesh links, area types, sampling.",
    "fps_controller": "First-person player rig: CharacterController movement/sprint/crouch/jump, mouse look, footsteps, interaction, stamina, flashlight, lean.",
    "weapons": "Weapons & combat: hitscan/projectile/melee attacks, ammo, recoil, muzzle flash, sway, hit reactions, damage receivers, weapon switching.",
    "enemy_ai": "Enemy actors: state-machine brain (patrol/chase/search/attack/stalk), sight/hearing senses, patrol routes, spawners.",
    "audio": "Audio sources, spatial audio, listener, mixer, reverb zones, occlusion, ambient beds, scare stingers, surface footsteps, dynamic music.",
    "rendering": "URP post-processing: render pipeline info, post-process volumes/profiles, vignette/bloom/DoF/chromatic aberration/motion blur/lens distortion/film grain/color grading, camera clear+fog tie-in, SSAO.",
    "vfx": "Particle systems, VFX Graph, decals, local fog pockets, trails, dust motes, blood splatter, breath fog.",
    "animation": "Animator Controllers, states/transitions/parameters, blend trees, avatar masks, animation events, IK constraints (Animation Rigging).",
    "gameplay": "ScriptableObject data/event channels, save/load state, game manager FSM, inventory, interactables, doors/keys, checkpoints, objectives.",
    "terrain": "Terrain creation, heightmap sculpting, texture/detail/tree painting, holes, wind zones, prop scattering.",
    "levelgen": "Procedural level layout, room/corridor generation, spawn points, LOD groups, lightmap UVs, occlusion culling, scene streaming, NavMesh validation.",
    "timeline": "Timeline assets, tracks/clips, signals, track bindings, camera-cut tracks, scripted scare sequences.",
    "input": "Input System action assets, maps/actions/bindings, action-map contexts, an SO-based InputReader pattern, rebinding UI, synthetic input injection.",
    "profiling": "CPU/GPU/memory profiler frames, memory snapshots, render statistics, and a performance-red-flags analysis composite.",
    "build": "Player builds, build-relevant Player Settings, UPM package management, and project-wide tags/layers/time/quality settings.",
}

_CONFIG_RECHECK_INTERVAL_SECONDS = 1.0

_disabled_groups: set[str] = set()
_default_active_groups: set[str] = {"core"}
_read_only_mode: bool = False
_config_mtime: Optional[float] = None
_last_config_check: float = 0.0


def _config_path() -> Path:
    return security.get_project_root() / "Library" / "MCP" / "tool_groups_config.json"


def _load_config() -> tuple[set[str], set[str], bool]:
    """Returns (disabledGroups, defaultActiveGroups, readOnlyMode) from disk, or
    ({}, {"core"}, False) if the file doesn't exist yet or can't be read -- both normal
    (no Unity project findable in this process, or the Tool Groups window has never been
    opened), not an error."""
    try:
        path = _config_path()
        data = json.loads(path.read_text())
    except (FileNotFoundError, OSError, ValueError):
        return set(), {"core"}, False

    disabled = {g for g in data.get("disabledGroups", []) if g != "core" and g in GROUP_CATALOG}
    default_active = {g for g in data.get("defaultActiveGroups", []) if g in GROUP_CATALOG}
    default_active.add("core")
    default_active -= disabled
    read_only_mode = bool(data.get("readOnlyMode", False))
    return disabled, default_active, read_only_mode


def _refresh_from_config_if_due() -> None:
    global _disabled_groups, _default_active_groups, _read_only_mode, _config_mtime, _last_config_check

    now = time.monotonic()
    if now - _last_config_check < _CONFIG_RECHECK_INTERVAL_SECONDS:
        return
    _last_config_check = now

    try:
        mtime = _config_path().stat().st_mtime
    except (FileNotFoundError, OSError):
        mtime = None

    # mtime is None both when the file has never existed AND right after it's been
    # deleted -- those are different transitions (nothing to do vs. "reload to pick up
    # the fallback defaults"), so a cached None must never short-circuit as "unchanged"
    # the way two equal real mtimes correctly do.
    if mtime is not None and mtime == _config_mtime:
        return
    _config_mtime = mtime
    _disabled_groups, _default_active_groups, _read_only_mode = _load_config()


def is_disabled(group: str) -> bool:
    _refresh_from_config_if_due()
    return group in _disabled_groups


def get_disabled_groups() -> set[str]:
    _refresh_from_config_if_due()
    return set(_disabled_groups)


def is_read_only_mode() -> bool:
    """Mirrors MCPToolGroupConfig.IsReadOnlyMode() on the Unity side -- see that file and
    docs/tool-scaling-strategy.md section 9. Re-checked on the same short throttle as
    disabled groups so a human flipping the toggle takes effect for an already-running
    session within about a second, the same "immediate, not just for new sessions"
    behavior disabling a group already has (unlike default-active groups, which only
    seed a fresh session)."""
    _refresh_from_config_if_due()
    return _read_only_mode


def _initial_active_groups() -> set[str]:
    _refresh_from_config_if_due()
    return set(_default_active_groups)


_active_groups: set[str] = _initial_active_groups()


def _write_live_state() -> None:
    """
    Writes Library/MCP/live_tool_state.json: this process's real, current active-group
    set (as opposed to tool_groups_config.json's defaultActiveGroups, which only seeds a
    *new* session and never reflects what a live session actually did with manage_tools).
    Read by the Unity Editor's Tool Groups window so a group the AI activated mid-session
    shows as genuinely active there instead of only "Active (default)"/"Inactive
    (default)" -- there is no live RPC channel Unity could otherwise use to ask this
    already-running Python process for its state on demand (the bridge protocol is
    Python-initiates-request/Unity-responds only, same reason tool_manifest.json and
    tool_groups_config.json both exist as files rather than calls). Best-effort, same as
    _write_tool_manifest in server.py: a failure here must never break a real tool call.
    """
    try:
        project_root = security.get_project_root()
    except FileNotFoundError:
        return

    state = {
        "pid": os.getpid(),
        "activeGroups": sorted(get_active_groups()),
        "updatedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }

    try:
        state_dir = project_root / "Library" / "MCP"
        state_dir.mkdir(parents=True, exist_ok=True)
        (state_dir / "live_tool_state.json").write_text(json.dumps(state))
    except OSError:
        pass


def get_active_groups() -> set[str]:
    return {g for g in _active_groups if not is_disabled(g)}


def is_active(group: str) -> bool:
    return group in _active_groups and not is_disabled(group)


def activate(group: str) -> None:
    _active_groups.add(group)
    _write_live_state()


def deactivate(group: str) -> None:
    _active_groups.discard(group)
    _write_live_state()


def reset() -> None:
    global _active_groups
    _active_groups = _initial_active_groups()
    _write_live_state()


# Seed the file once at import time too, so the window has something to show even
# before the first activate/deactivate/reset call of a session (i.e. right after the
# Python server starts, showing whatever defaultActiveGroups seeded this session with).
_write_live_state()
