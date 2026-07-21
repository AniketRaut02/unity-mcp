"""
Composite "workflow" tools — Phase 5.

Per the original architecture plan (§8): a composite tool is built from Layer-0
atomic tools (create_gameobject, add_component, ...) but exposed to the MCP
client as a single higher-level tool. Two ways to build one: Python-side
composition (a function here that calls bridge.call()/bridge.batch() several
times, possibly with real branching logic) or Unity-side composition (a new
[MCPTool] C# method). Start every new composite in Python — cheap to iterate,
no recompile needed to change — and only promote a proven one to C# if
performance ever actually demands it. Nothing here has needed that yet.

None of these map to a single [MCPTool] C# method, which is *why* they live
here rather than in Unity's reflection-discovered registry: their entire job
is calling other tools, sometimes with loops/conditionals Unity's tool schema
system has no way to express. batch_execute (moved here from server.py, where
it started as a one-off special case) is workflow #0; everything since follows
the same registration pattern.
"""
import asyncio
import logging
import time
from dataclasses import dataclass
from typing import Any, Awaitable, Callable, Optional

from .bridge_client import BridgeError, UnityBridgeClient
from . import groups as tool_groups

logger = logging.getLogger("unity_mcp.workflows")

WorkflowHandler = Callable[[UnityBridgeClient, dict], Awaitable[Any]]


@dataclass
class Workflow:
    name: str
    description: str
    schema: dict
    handler: WorkflowHandler
    group: str = "core"


_REGISTRY: dict[str, Workflow] = {}


def workflow(name: str, description: str, schema: dict, group: str = "core"):
    """Decorator that registers a Python-side composite tool under `name`."""

    def decorator(fn: WorkflowHandler) -> WorkflowHandler:
        if name in _REGISTRY:
            raise ValueError(f"Duplicate workflow name '{name}' — check for a copy-paste registration.")
        _REGISTRY[name] = Workflow(name=name, description=description, schema=schema, handler=fn, group=group)
        return fn

    return decorator


def all_workflows() -> list[Workflow]:
    return list(_REGISTRY.values())


def get_workflow(name: str) -> Optional[Workflow]:
    return _REGISTRY.get(name)


# ---------------------------------------------------------------------------
# batch_execute
# ---------------------------------------------------------------------------

_BATCH_EXECUTE_SCHEMA = {
    "type": "object",
    "properties": {
        "calls": {
            "type": "array",
            "description": "Ordered list of sub-calls to run in one round trip. Results come back in the same order.",
            "items": {
                "type": "object",
                "properties": {
                    "tool": {"type": "string", "description": "Name of a Unity tool, e.g. 'create_gameobject'."},
                    "args": {"type": "object", "description": "Arguments for that tool, same shape as calling it directly."},
                },
                "required": ["tool"],
            },
        }
    },
    "required": ["calls"],
}

_BATCH_EXECUTE_DESCRIPTION = (
    "Runs multiple Unity tool calls in a single round trip instead of one at a time -- use this whenever making "
    "several related tool calls back to back (e.g. creating a GameObject, adding a component, and setting several "
    "fields) to reduce latency. Every normal safety mechanism still applies per sub-call (destructive tools still "
    "need confirm=true, etc). Slow/domain-reload-triggering tools (create_script, update_script, delete_script) are "
    "rejected inside a batch -- call those individually."
)


@workflow("batch_execute", _BATCH_EXECUTE_DESCRIPTION, _BATCH_EXECUTE_SCHEMA)
async def _batch_execute(bridge: UnityBridgeClient, args: dict) -> Any:
    calls = args.get("calls", [])
    return await bridge.batch(calls)


# ---------------------------------------------------------------------------
# Behavior Tree framework scaffolding + composite tree-building tools
# ---------------------------------------------------------------------------

# Deliberately minimal but real and working: two composites (Sequence, Selector),
# one extensible leaf (ActionNode), and a runner that builds the runtime tree from
# the GameObject hierarchy it's attached to. This is what makes the tree editable
# via the MCP scene tools already built in Phases 1-3 (reparent_gameobject moves a
# node between parents, add_component/remove_component changes a node's type,
# delete_gameobject removes one) rather than needing any new Editor machinery.
_BT_FRAMEWORK_FILES: dict[str, str] = {
    "Scripts/BehaviorTree/BTStatus.cs": """namespace BehaviorTree
{
    /// <summary>Result of ticking a behavior tree node this frame.</summary>
    public enum BTStatus
    {
        Running,
        Success,
        Failure
    }
}
""",
    "Scripts/BehaviorTree/BTNode.cs": """using System.Collections.Generic;
using UnityEngine;

namespace BehaviorTree
{
    /// <summary>
    /// Base class for every node in the tree. Nodes are plain C# objects, rebuilt at
    /// runtime by BTRunner from the GameObject hierarchy it's attached to -- each child
    /// GameObject with a BTNodeComponent becomes a child node, recursively.
    /// </summary>
    public abstract class BTNode
    {
        public string Name = "Node";
        public abstract BTStatus Tick(float deltaTime);

        /// <summary>Called once when this node transitions from not-running to running.</summary>
        public virtual void OnEnter() {}

        /// <summary>Called once when this node stops running (success, failure, or interrupted).</summary>
        public virtual void OnExit(BTStatus result) {}
    }

    /// <summary>Base for nodes that have children (Sequence, Selector, ...).</summary>
    public abstract class BTComposite : BTNode
    {
        public readonly List<BTNode> Children = new List<BTNode>();
    }

    /// <summary>
    /// Bridges a BTNode (plain C# logic) to a MonoBehaviour so it can be attached as a
    /// real Unity component -- this is exactly what the MCP scene tools (add_component,
    /// reparent_gameobject, delete_gameobject, ...) manipulate. Subclass this for each
    /// concrete node type; see SequenceComponent / SelectorComponent / ActionNodeComponent.
    /// </summary>
    public abstract class BTNodeComponent : MonoBehaviour
    {
        public abstract BTNode CreateNode();
    }
}
""",
    "Scripts/BehaviorTree/Sequence.cs": """namespace BehaviorTree
{
    /// <summary>Runs children in order; stops and fails on the first child that fails; succeeds only if all children succeed.</summary>
    public class Sequence : BTComposite
    {
        private int _current = 0;

        public override BTStatus Tick(float deltaTime)
        {
            while (_current < Children.Count)
            {
                var status = Children[_current].Tick(deltaTime);
                if (status == BTStatus.Running) return BTStatus.Running;
                if (status == BTStatus.Failure) { _current = 0; return BTStatus.Failure; }
                _current++;
            }
            _current = 0;
            return BTStatus.Success;
        }
    }

    public class SequenceComponent : BTNodeComponent
    {
        public override BTNode CreateNode() => new Sequence { Name = name };
    }
}
""",
    "Scripts/BehaviorTree/Selector.cs": """namespace BehaviorTree
{
    /// <summary>Runs children in order; stops and succeeds on the first child that succeeds; fails only if all children fail.</summary>
    public class Selector : BTComposite
    {
        private int _current = 0;

        public override BTStatus Tick(float deltaTime)
        {
            while (_current < Children.Count)
            {
                var status = Children[_current].Tick(deltaTime);
                if (status == BTStatus.Running) return BTStatus.Running;
                if (status == BTStatus.Success) { _current = 0; return BTStatus.Success; }
                _current++;
            }
            _current = 0;
            return BTStatus.Failure;
        }
    }

    public class SelectorComponent : BTNodeComponent
    {
        public override BTNode CreateNode() => new Selector { Name = name };
    }
}
""",
    "Scripts/BehaviorTree/ActionNode.cs": """namespace BehaviorTree
{
    /// <summary>
    /// A leaf node that does actual game work. This default implementation always
    /// succeeds immediately -- it exists so create_behavior_tree has a concrete leaf
    /// type to instantiate out of the box. Write your own BTNode subclass (following
    /// ActionNodeComponent's pattern) for real game logic and wire it in with
    /// add_behavior_tree_node or a plain add_component call.
    /// </summary>
    public class ActionNode : BTNode
    {
        public override BTStatus Tick(float deltaTime) => BTStatus.Success;
    }

    public class ActionNodeComponent : BTNodeComponent
    {
        public override BTNode CreateNode() => new ActionNode { Name = name };
    }
}
""",
    "Scripts/BehaviorTree/BTRunner.cs": """using UnityEngine;

namespace BehaviorTree
{
    /// <summary>
    /// Attach to the root GameObject of a behavior tree (alongside a root composite's
    /// component, e.g. SequenceComponent). Builds the runtime BTNode tree once from
    /// this GameObject's own hierarchy and ticks the root every frame. Rebuilding from
    /// the GameObject hierarchy at Start is what lets the MCP scene tools edit the
    /// tree's structure between runs without any Behavior-Tree-specific Editor tooling.
    /// </summary>
    public class BTRunner : MonoBehaviour
    {
        private BTNode _root;

        private void Start()
        {
            _root = BuildNode(transform);
        }

        private void Update()
        {
            _root?.Tick(Time.deltaTime);
        }

        private static BTNode BuildNode(Transform t)
        {
            var component = t.GetComponent<BTNodeComponent>();
            if (component == null) return null;

            var node = component.CreateNode();

            if (node is BTComposite composite)
            {
                foreach (Transform child in t)
                {
                    var childNode = BuildNode(child);
                    if (childNode != null) composite.Children.Add(childNode);
                }
            }

            return node;
        }
    }
}
""",
}

# JSON Schema fragment reused for both create_behavior_tree's top-level children
# and add_behavior_tree_node's nested children -- a recursive node spec.
_NODE_SPEC_SCHEMA = {
    "type": "object",
    "properties": {
        "name": {"type": "string", "description": "GameObject name for this node."},
        "type": {"type": "string", "enum": ["Sequence", "Selector", "Action"], "description": "Node behavior."},
        "children": {
            "type": "array",
            "description": "Nested child nodes (only meaningful for Sequence/Selector).",
            "items": {"$ref": "#/definitions/node"},
        },
    },
    "required": ["name", "type"],
}


async def _scaffold_bt_framework_impl(bridge: UnityBridgeClient) -> tuple[list[str], list[str]]:
    """
    Writes any missing Behavior Tree framework file, in order, via the existing
    create_script + update_script atomic tools -- no C# changes were needed to build
    this composite layer, which is the point being demonstrated as much as the BT
    framework itself. Idempotent: a file that already exists is left completely
    untouched (never overwritten), so re-running this after someone has hand-edited
    e.g. ActionNode.cs for their own game logic is always safe.
    """
    created: list[str] = []
    skipped: list[str] = []

    for relative_path, content in _BT_FRAMEWORK_FILES.items():
        try:
            await bridge.call("create_script", {"path": relative_path, "template": "PlainClass"})
        except BridgeError as e:
            if "already exists" in str(e):
                skipped.append(relative_path)
                continue
            raise
        await bridge.call("update_script", {"path": relative_path, "content": content})
        created.append(relative_path)

    return created, skipped


async def _wait_for_compile(bridge: UnityBridgeClient, timeout: float = 60.0, poll_interval: float = 0.5) -> None:
    """
    Polls get_compile_status until Unity finishes the domain reload the just-written
    scripts triggered. Only called when scaffolding actually created new files --
    if everything was already present, nothing was written and there's nothing to wait for.
    """
    start = time.monotonic()
    await asyncio.sleep(poll_interval)  # give Unity a moment to actually start compiling
    while True:
        status = await bridge.call("get_compile_status", {})
        if not status.get("isCompiling"):
            error_count = status.get("errorCount", 0)
            if error_count > 0:
                raise BridgeError(
                    f"Compilation finished with {error_count} error(s) after scaffolding the Behavior Tree "
                    f"framework: {status.get('errors')}"
                )
            return
        if time.monotonic() - start > timeout:
            raise BridgeError(f"Timed out after {timeout}s waiting for Unity to finish compiling.")
        await asyncio.sleep(poll_interval)


async def _create_node_tree(bridge: UnityBridgeClient, parent_path: str, specs: list[dict], created_paths: list[str]) -> None:
    """Recursively creates a list of sibling node specs under parent_path, depth-first."""
    for spec in specs:
        result = await bridge.call("create_gameobject", {"name": spec["name"], "parentPath": parent_path})
        node_path = result["path"]
        await bridge.call("add_component", {"path": node_path, "typeName": f"{spec['type']}Component"})
        created_paths.append(node_path)

        nested = spec.get("children") or []
        if nested:
            await _create_node_tree(bridge, node_path, nested, created_paths)


@workflow(
    "scaffold_behavior_tree_framework",
    "Generates the core Behavior Tree runtime C# scripts (BTNode, Sequence, Selector, ActionNode, BTRunner) into "
    "Assets/Scripts/BehaviorTree/ if they don't already exist there. Safe to call repeatedly -- existing files are "
    "left completely untouched, never overwritten. create_behavior_tree calls this automatically, so you only need "
    "to call it directly if you want the framework in place before building any tree (e.g. to review or hand-edit "
    "ActionNode.cs first).",
    {"type": "object", "properties": {}, "required": []},
    group="behavior_tree",
)
async def _scaffold_behavior_tree_framework(bridge: UnityBridgeClient, args: dict) -> Any:
    created, skipped = await _scaffold_bt_framework_impl(bridge)
    if created:
        await _wait_for_compile(bridge)
    return {"created": created, "skipped": skipped}


@workflow(
    "create_behavior_tree",
    "Builds a complete behavior tree in the active scene from a nested spec: a root GameObject with a BTRunner and "
    "a root composite (Sequence or Selector), plus every descendant node as a child GameObject. Automatically "
    "scaffolds the BT framework first if it's missing (and waits for Unity to finish compiling before building the "
    "tree). Returns every created GameObject's hierarchy path, in creation order.",
    {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "GameObject name for the tree's root."},
            "rootType": {"type": "string", "enum": ["Sequence", "Selector"], "description": "Composite type for the root node."},
            "children": {
                "type": "array",
                "description": "Immediate children of the root (each may itself have nested children).",
                "items": {"$ref": "#/definitions/node"},
            },
        },
        "required": ["name", "rootType"],
        "definitions": {"node": _NODE_SPEC_SCHEMA},
    },
    group="behavior_tree",
)
async def _create_behavior_tree(bridge: UnityBridgeClient, args: dict) -> Any:
    created, skipped = await _scaffold_bt_framework_impl(bridge)
    if created:
        await _wait_for_compile(bridge)

    name = args["name"]
    root_type = args["rootType"]
    children_spec = args.get("children") or []

    root_result = await bridge.call("create_gameobject", {"name": name})
    root_path = root_result["path"]
    await bridge.call("add_component", {"path": root_path, "typeName": f"{root_type}Component"})
    await bridge.call("add_component", {"path": root_path, "typeName": "BTRunner"})

    created_paths = [root_path]
    await _create_node_tree(bridge, root_path, children_spec, created_paths)

    return {"rootPath": root_path, "nodes": created_paths, "frameworkFilesCreated": created, "frameworkFilesSkipped": skipped}


@workflow(
    "add_behavior_tree_node",
    "Adds a node (Sequence/Selector/Action) under an existing behavior tree node by path, optionally with its own "
    "nested children. Use this to extend a tree created by create_behavior_tree without rebuilding it from scratch. "
    "Does not scaffold the framework -- if this fails because the node component types don't exist yet, run "
    "create_behavior_tree or scaffold_behavior_tree_framework first.",
    {
        "type": "object",
        "properties": {
            "parentPath": {"type": "string", "description": "Hierarchy path of the existing node to add this one under."},
            "name": {"type": "string"},
            "type": {"type": "string", "enum": ["Sequence", "Selector", "Action"]},
            "children": {"type": "array", "items": {"$ref": "#/definitions/node"}},
        },
        "required": ["parentPath", "name", "type"],
        "definitions": {"node": _NODE_SPEC_SCHEMA},
    },
    group="behavior_tree",
)
async def _add_behavior_tree_node(bridge: UnityBridgeClient, args: dict) -> Any:
    parent_path = args["parentPath"]

    result = await bridge.call("create_gameobject", {"name": args["name"], "parentPath": parent_path})
    node_path = result["path"]
    await bridge.call("add_component", {"path": node_path, "typeName": f"{args['type']}Component"})

    created_paths = [node_path]
    nested = args.get("children") or []
    if nested:
        await _create_node_tree(bridge, node_path, nested, created_paths)

    return {"path": node_path, "nodes": created_paths}


# ---------------------------------------------------------------------------
# manage_tools — controls which groups are visible in list_tools()
# ---------------------------------------------------------------------------

@workflow(
    "manage_tools",
    "Controls which tool groups are visible in this session's tool list. Most tools are hidden by default to keep "
    "the visible tool list focused (fewer tokens, better routing) -- activate the group you need before calling "
    "its tools. Actions: list_groups (every group, its description, whether it's active, and which tools it "
    "contains), activate (make a group's tools visible), deactivate ('core' cannot be deactivated -- it holds the "
    "essential scene/component/query tools plus batch_execute and this tool itself), reset (back to only 'core').",
    {
        "type": "object",
        "properties": {
            "action": {"type": "string", "enum": ["list_groups", "activate", "deactivate", "reset"]},
            "group": {
                "type": "string",
                "description": f"Required for activate/deactivate. One of: {', '.join(tool_groups.GROUP_CATALOG)}",
            },
        },
        "required": ["action"],
    },
    group="core",
)
async def _manage_tools(bridge: UnityBridgeClient, args: dict) -> Any:
    action = args["action"]

    if action == "reset":
        tool_groups.reset()
        return {"active": sorted(tool_groups.get_active_groups())}

    if action == "list_groups":
        try:
            unity_tools = await bridge.list_tools()
        except BridgeError:
            unity_tools = []

        tool_names_by_group: dict[str, list[str]] = {g: [] for g in tool_groups.GROUP_CATALOG}
        for t in unity_tools:
            g = t.get("group") or "core"
            tool_names_by_group.setdefault(g, []).append(t["name"])
        for wf in all_workflows():
            tool_names_by_group.setdefault(wf.group, []).append(wf.name)

        return {
            "groups": [
                {
                    "group": name,
                    "description": description,
                    "active": tool_groups.is_active(name),
                    "tools": sorted(tool_names_by_group.get(name, [])),
                }
                for name, description in tool_groups.GROUP_CATALOG.items()
            ]
        }

    if action in ("activate", "deactivate"):
        group_name = args.get("group")
        if not group_name:
            raise BridgeError("'group' is required for activate/deactivate.")
        if group_name not in tool_groups.GROUP_CATALOG:
            raise BridgeError(f"Unknown group '{group_name}'. Valid groups: {', '.join(tool_groups.GROUP_CATALOG)}")

        if action == "activate":
            tool_groups.activate(group_name)
        else:
            if group_name == "core":
                raise BridgeError("'core' cannot be deactivated -- it contains the tools every session needs.")
            tool_groups.deactivate(group_name)

        return {"active": sorted(tool_groups.get_active_groups())}

    raise BridgeError(f"Unknown action '{action}'.")


# Imported for side effects only -- registers any composite tools the visual tool builder
# (Window -> Unity MCP -> Tool Builder) has generated into custom_workflows.py. Imported
# at the very end of this file, after `workflow` (the decorator) is fully defined, since
# custom_workflows.py imports it back via `from .workflows import workflow`.
from . import custom_workflows  # noqa: F401,E402
