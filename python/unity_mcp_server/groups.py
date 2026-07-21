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
"""

GROUP_CATALOG: dict[str, str] = {
    "core": "Essential scene, component, and query tools, plus batch_execute and manage_tools. Always active.",
    "scripting": "Create/read/update/delete C# scripts; check compile status.",
    "physics": "Colliders, Rigidbody configuration, forces, raycasting.",
    "assets": "Prefabs, materials, ScriptableObjects, and generic asset listing/deletion.",
    "ui": "UGUI Canvas, buttons, layout groups, RectTransform.",
    "behavior_tree": "Composite tools for scaffolding and building custom Behavior Trees.",
    "inspection": "Screenshots, console log reads, and compile diagnostics — lets an agent check its own work.",
    "testing": "Play mode control (enter/exit/pause) and automated test running.",
}

_active_groups: set[str] = {"core"}


def get_active_groups() -> set[str]:
    return set(_active_groups)


def is_active(group: str) -> bool:
    return group in _active_groups


def activate(group: str) -> None:
    _active_groups.add(group)


def deactivate(group: str) -> None:
    _active_groups.discard(group)


def reset() -> None:
    global _active_groups
    _active_groups = {"core"}
