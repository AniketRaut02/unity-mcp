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
import json
import logging
import math
import random
import time
from dataclasses import dataclass
from typing import Any, Awaitable, Callable, Optional

from .bridge_client import BridgeError, UnityBridgeClient
from . import groups as tool_groups
from . import tool_search

logger = logging.getLogger("unity_mcp.workflows")

WorkflowHandler = Callable[[UnityBridgeClient, dict], Awaitable[Any]]


@dataclass
class Workflow:
    name: str
    description: str
    schema: dict
    handler: WorkflowHandler
    group: str = "core"
    # MCP spec annotations (see docs/tool-scaling-strategy.md section 7) -- mirrors the
    # C#-side [MCPTool(destructive:, readOnly:)] attribute params, but hand-set here per
    # composite instead of audited by a script, since there are ~80 of these rather than
    # ~230, and their handler bodies are visible in this same file. `destructive=True` means
    # a normal (non-confirm-gated) call irreversibly removes/replaces something the caller
    # didn't create as scratch state within the same call -- most composites only mutate
    # things they create fresh, which doesn't count.
    destructive: bool = False
    read_only: bool = False


_REGISTRY: dict[str, Workflow] = {}


def workflow(
    name: str,
    description: str,
    schema: dict,
    group: str = "core",
    destructive: bool = False,
    read_only: bool = False,
):
    """Decorator that registers a Python-side composite tool under `name`."""

    def decorator(fn: WorkflowHandler) -> WorkflowHandler:
        if name in _REGISTRY:
            raise ValueError(f"Duplicate workflow name '{name}' — check for a copy-paste registration.")
        _REGISTRY[name] = Workflow(
            name=name,
            description=description,
            schema=schema,
            handler=fn,
            group=group,
            destructive=destructive,
            read_only=read_only,
        )
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
# align_gameobjects / snap_to_ground -- composites over get_transform /
# translate_gameobject / raycast, moving objects via WORLD-space translation
# (not set_transform's local-only position) so both are correct regardless of
# each object's parent, not just for scene-root objects.
# ---------------------------------------------------------------------------

_ALIGN_GAMEOBJECTS_SCHEMA = {
    "type": "object",
    "properties": {
        "paths": {
            "type": "array",
            "items": {"type": "string"},
            "description": "Hierarchy paths of the GameObjects to align/distribute (at least 2).",
        },
        "axis": {"type": "string", "enum": ["x", "y", "z"], "description": "World axis to align/distribute along."},
        "mode": {
            "type": "string",
            "enum": ["align", "distribute"],
            "description": "align: move every object to one coordinate on the axis. distribute: space them evenly between the current lowest and highest, preserving relative order.",
        },
        "value": {
            "type": "number",
            "description": "For 'align' only: the world coordinate to align to. Omit to use the first path's current position on that axis.",
        },
    },
    "required": ["paths", "axis", "mode"],
}


@workflow(
    "align_gameobjects",
    "Aligns or evenly distributes a set of GameObjects along one world axis (X, Y, or Z). Moves each object via "
    "world-space translation, so it's correct even when the objects have different parents (unlike a raw "
    "local-position write). 'align' moves every object to the same coordinate on that axis (the first path's "
    "current position, or an explicit 'value'). 'distribute' spaces every object evenly between the current lowest "
    "and highest position on that axis, preserving their relative order. Needs at least 2 paths.",
    _ALIGN_GAMEOBJECTS_SCHEMA,
)
async def _align_gameobjects(bridge: UnityBridgeClient, args: dict) -> Any:
    paths = args["paths"]
    axis = args["axis"]
    mode = args["mode"]

    if len(paths) < 2:
        raise BridgeError("align_gameobjects needs at least 2 paths.")

    delta_key = {"x": "deltaX", "y": "deltaY", "z": "deltaZ"}[axis]

    positions = []
    for path in paths:
        transform = await bridge.call("get_transform", {"path": path})
        positions.append(transform["worldPosition"][axis])

    if mode == "align":
        target_value = args.get("value", positions[0])
        targets = [target_value] * len(paths)
    elif mode == "distribute":
        order = sorted(range(len(paths)), key=lambda i: positions[i])
        min_v, max_v = positions[order[0]], positions[order[-1]]
        n = len(order) - 1
        targets = [0.0] * len(paths)
        for rank, idx in enumerate(order):
            targets[idx] = min_v if n == 0 else min_v + (max_v - min_v) * (rank / n)
    else:
        raise BridgeError(f"Unknown mode '{mode}'. Must be 'align' or 'distribute'.")

    for path, current, target in zip(paths, positions, targets):
        delta = target - current
        if abs(delta) > 1e-6:
            await bridge.call("translate_gameobject", {"path": path, delta_key: delta, "worldSpace": True})

    return {"paths": paths, "axis": axis, "mode": mode, "targetValues": dict(zip(paths, targets))}


@workflow(
    "snap_to_ground",
    "Moves a GameObject straight down until it hits a collider, placing it at the hit point plus an optional "
    "upward clearance offset (e.g. half the object's height, so it doesn't clip into the ground). Correct for "
    "parented objects (moves via world-space translation, not local position). Fails without moving the object if "
    "nothing is hit within maxDistance.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to snap."},
            "maxDistance": {"type": "number", "description": "Maximum raycast distance downward. Defaults to 1000."},
            "clearance": {
                "type": "number",
                "description": "Distance to offset upward from the hit point. Defaults to 0.",
            },
        },
        "required": ["path"],
    },
)
async def _snap_to_ground(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    max_distance = args.get("maxDistance", 1000.0)
    clearance = args.get("clearance", 0.0)

    hit = await bridge.call("raycast", {"fromPath": path, "dirX": 0, "dirY": -1, "dirZ": 0, "maxDistance": max_distance})
    if not hit.get("hit"):
        raise BridgeError(f"snap_to_ground: no collider found below '{path}' within {max_distance} units.")

    transform = await bridge.call("get_transform", {"path": path})
    current = transform["worldPosition"]
    target = hit["point"]

    delta_x = target["x"] - current["x"]
    delta_y = (target["y"] + clearance) - current["y"]
    delta_z = target["z"] - current["z"]

    await bridge.call(
        "translate_gameobject",
        {"path": path, "deltaX": delta_x, "deltaY": delta_y, "deltaZ": delta_z, "worldSpace": True},
    )

    return {"path": path, "groundPoint": target, "clearance": clearance}


def _flatten_hierarchy_paths(nodes: list[dict]) -> list[str]:
    paths = []
    for node in nodes:
        paths.append(node["path"])
        paths.extend(_flatten_hierarchy_paths(node.get("children", [])))
    return paths


@workflow(
    "replace_prefab_instances",
    "Finds every instance of one prefab in the active scene and replaces each with a new instance of a different "
    "prefab, preserving position/rotation/scale/parent/name. Use to swap a placeholder prefab for a finished one "
    "across an entire scene in one call, rather than doing it instance by instance.",
    {
        "type": "object",
        "properties": {
            "oldPrefabPath": {
                "type": "string",
                "description": "Path relative to Assets/ of the prefab whose instances should be replaced.",
            },
            "newPrefabPath": {
                "type": "string",
                "description": "Path relative to Assets/ of the prefab to replace them with.",
            },
        },
        "required": ["oldPrefabPath", "newPrefabPath"],
    },
    group="assets",
    destructive=True,
)
async def _replace_prefab_instances(bridge: UnityBridgeClient, args: dict) -> Any:
    old_path = args["oldPrefabPath"]
    new_path = args["newPrefabPath"]

    hierarchy = await bridge.call("get_scene_hierarchy", {"limit": 10000})
    all_paths = _flatten_hierarchy_paths(hierarchy["roots"])

    matches = []
    for path in all_paths:
        try:
            overrides = await bridge.call("get_prefab_overrides", {"path": path})
        except BridgeError:
            continue  # not part of a prefab instance at all -- not an error, just not a candidate
        if overrides.get("sourcePrefabPath") == old_path:
            matches.append(path)

    replacements = []
    for path in matches:
        transform = await bridge.call("get_transform", {"path": path})
        parts = path.split("/")
        name = parts[-1]
        parent_path = "/".join(parts[:-1]) or None

        await bridge.call("delete_gameobject", {"path": path, "confirm": True})

        pos = transform["localPosition"]
        instantiate_result = await bridge.call(
            "instantiate_prefab",
            {"assetPath": new_path, "parentPath": parent_path, "posX": pos["x"], "posY": pos["y"], "posZ": pos["z"]},
        )
        new_instance_path = instantiate_result["path"]

        rot = transform["localEulerAngles"]
        scale = transform["localScale"]
        await bridge.call(
            "set_transform",
            {
                "path": new_instance_path,
                "rotX": rot["x"], "rotY": rot["y"], "rotZ": rot["z"],
                "scaleX": scale["x"], "scaleY": scale["y"], "scaleZ": scale["z"],
            },
        )
        await bridge.call("rename_gameobject", {"path": new_instance_path, "newName": name})

        final_path = f"{parent_path}/{name}" if parent_path else name
        replacements.append({"oldPath": path, "newPath": final_path})

    return {"replacedCount": len(replacements), "replacements": replacements}


# ---------------------------------------------------------------------------
# add_trigger_volume -- a trigger collider plus a small reusable relay script,
# scaffolded the same idempotent way scaffold_behavior_tree_framework is.
# ---------------------------------------------------------------------------

_TRIGGER_RELAY_PATH = "Scripts/MCP/MCPTriggerRelay.cs"
_TRIGGER_RELAY_CONTENT = """using UnityEngine;
using UnityEngine.Events;

public class MCPTriggerRelay : MonoBehaviour
{
    public UnityEvent<Collider> onTriggerEnter;
    public UnityEvent<Collider> onTriggerExit;

    private void OnTriggerEnter(Collider other)
    {
        onTriggerEnter?.Invoke(other);
    }

    private void OnTriggerExit(Collider other)
    {
        onTriggerExit?.Invoke(other);
    }
}
"""


async def _scaffold_trigger_relay_script(bridge: UnityBridgeClient) -> bool:
    """Returns True if the file was just created (caller should wait for compile), False if it already existed."""
    try:
        await bridge.call("create_script", {"path": _TRIGGER_RELAY_PATH, "template": "PlainClass"})
    except BridgeError as e:
        if "already exists" in str(e):
            return False
        raise
    await bridge.call("update_script", {"path": _TRIGGER_RELAY_PATH, "content": _TRIGGER_RELAY_CONTENT})
    return True


@workflow(
    "add_trigger_volume",
    "Creates a trigger collider on a GameObject (Box or Sphere) and attaches a small 'MCPTriggerRelay' component "
    "exposing onTriggerEnter/onTriggerExit UnityEvents, so other scripts (wired via the Inspector or "
    "wire_object_reference) can react to the trigger without writing OnTriggerEnter/OnTriggerExit boilerplate "
    "themselves. Scaffolds the relay script into Assets/Scripts/MCP/ the first time it's needed; safe to call "
    "repeatedly.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to add the trigger to."},
            "shape": {"type": "string", "enum": ["Box", "Sphere"], "description": "Collider shape. Defaults to Box."},
            "size": {"type": "number", "description": "Box only: uniform size on all 3 axes. Defaults to 1."},
            "radius": {"type": "number", "description": "Sphere only. Defaults to 0.5."},
        },
        "required": ["path"],
    },
    group="physics",
)
async def _add_trigger_volume(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    shape = args.get("shape", "Box")
    size = args.get("size", 1.0)
    radius = args.get("radius", 0.5)

    if shape == "Box":
        collider_args = {"path": path, "type": shape, "isTrigger": True, "sizeX": size, "sizeY": size, "sizeZ": size}
    elif shape == "Sphere":
        collider_args = {"path": path, "type": shape, "isTrigger": True, "radius": radius}
    else:
        raise BridgeError(f"Unknown shape '{shape}'. Must be 'Box' or 'Sphere'.")

    await bridge.call("add_collider", collider_args)

    created = await _scaffold_trigger_relay_script(bridge)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPTriggerRelay"})

    return {"path": path, "shape": shape, "relayScriptCreated": created}


# ---------------------------------------------------------------------------
# add_flicker_light -- attaches a small reusable flicker/strobe controller to
# an existing Light, scaffolded the same idempotent way as MCPTriggerRelay.
# ---------------------------------------------------------------------------

_FLICKER_LIGHT_PATH = "Scripts/MCP/MCPFlickerLight.cs"
_FLICKER_LIGHT_CONTENT = """using UnityEngine;

[RequireComponent(typeof(Light))]
public class MCPFlickerLight : MonoBehaviour
{
    public float minIntensity = 0.2f;
    public float maxIntensity = 1.5f;
    public float flickerSpeed = 10f;
    public bool useRandomNoise = true;

    private Light _light;

    private void Awake()
    {
        _light = GetComponent<Light>();
    }

    private void Update()
    {
        if (_light == null) return;

        float t = useRandomNoise
            ? Mathf.PerlinNoise(Time.time * flickerSpeed, 0f)
            : (Mathf.Sin(Time.time * flickerSpeed) + 1f) * 0.5f;

        _light.intensity = Mathf.Lerp(minIntensity, maxIntensity, t);
    }
}
"""


async def _scaffold_flicker_light_script(bridge: UnityBridgeClient) -> bool:
    """Returns True if the file was just created (caller should wait for compile), False if it already existed."""
    try:
        await bridge.call("create_script", {"path": _FLICKER_LIGHT_PATH, "template": "PlainClass"})
    except BridgeError as e:
        if "already exists" in str(e):
            return False
        raise
    await bridge.call("update_script", {"path": _FLICKER_LIGHT_PATH, "content": _FLICKER_LIGHT_CONTENT})
    return True


@workflow(
    "add_flicker_light",
    "Attaches a small 'MCPFlickerLight' component to a GameObject that randomizes (or sine-waves) its Light's "
    "intensity every frame -- the classic failing-bulb/strobe horror atmosphere effect. Adds a Light automatically "
    "if the target doesn't already have one (via [RequireComponent]). Scaffolds the controller script into "
    "Assets/Scripts/MCP/ the first time it's needed; safe to call repeatedly.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to attach the flicker to (its Light, or a new one)."},
            "minIntensity": {"type": "number", "description": "Lowest intensity reached while flickering. Defaults to 0.2."},
            "maxIntensity": {"type": "number", "description": "Highest intensity reached while flickering. Defaults to 1.5."},
            "flickerSpeed": {"type": "number", "description": "How fast the flicker cycles. Defaults to 10."},
            "useRandomNoise": {"type": "boolean", "description": "True (default) for irregular Perlin-noise flicker; false for a smooth, regular sine-wave pulse."},
        },
        "required": ["path"],
    },
    group="lighting",
)
async def _add_flicker_light(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]

    created = await _scaffold_flicker_light_script(bridge)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPFlickerLight"})

    field_names, values = [], []
    for key in ("minIntensity", "maxIntensity", "flickerSpeed", "useRandomNoise"):
        if key in args:
            field_names.append(key)
            values.append(str(args[key]))
    if field_names:
        await bridge.call("set_component_properties_batch", {
            "path": path,
            "typeName": "MCPFlickerLight",
            "fieldNames": field_names,
            "values": values,
        })

    return {"path": path, "relayScriptCreated": created}


# ---------------------------------------------------------------------------
# spawn_emissive_source -- an emissive-material prop plus (optionally) a real
# Light nearby, so it reads as a local light cue rather than just a bright
# surface (emissive materials alone don't cast light onto other objects
# without a lightmap bake).
# ---------------------------------------------------------------------------


@workflow(
    "spawn_emissive_source",
    "Creates a primitive with an emissive material (a glowing prop -- lantern, terminal screen, ember) and, by "
    "default, a real Point Light alongside it so it actually illuminates its surroundings -- an emissive material's "
    "glow by itself doesn't light other objects in realtime without a lightmap bake.",
    {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "Name for the new primitive GameObject, and its material asset."},
            "parentPath": {"type": "string", "description": "Hierarchy path to parent the new objects under. Omit for scene root."},
            "primitiveType": {
                "type": "string",
                "enum": ["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"],
                "description": "Which primitive to spawn. Defaults to Sphere.",
            },
            "x": {"type": "number", "description": "World-space X position. Defaults to 0."},
            "y": {"type": "number", "description": "World-space Y position. Defaults to 0."},
            "z": {"type": "number", "description": "World-space Z position. Defaults to 0."},
            "colorR": {"type": "number", "description": "Emission color red component (0-1). Defaults to 1."},
            "colorG": {"type": "number", "description": "Emission color green component (0-1). Defaults to 1."},
            "colorB": {"type": "number", "description": "Emission color blue component (0-1). Defaults to 1."},
            "emissionIntensity": {"type": "number", "description": "Multiplier applied to the emission color. Defaults to 2."},
            "addPointLight": {"type": "boolean", "description": "Whether to also add a real Point Light as a child. Defaults to true."},
            "lightRange": {"type": "number", "description": "Point light range in meters, if addPointLight. Defaults to 5."},
            "lightIntensity": {"type": "number", "description": "Point light intensity, if addPointLight. Defaults to 1."},
        },
        "required": ["name"],
    },
    group="lighting",
)
async def _spawn_emissive_source(bridge: UnityBridgeClient, args: dict) -> Any:
    name = args["name"]
    parent_path = args.get("parentPath")
    primitive_type = args.get("primitiveType", "Sphere")
    x, y, z = args.get("x", 0.0), args.get("y", 0.0), args.get("z", 0.0)
    r, g, b = args.get("colorR", 1.0), args.get("colorG", 1.0), args.get("colorB", 1.0)
    emission_intensity = args.get("emissionIntensity", 2.0)
    add_point_light = args.get("addPointLight", True)

    primitive_result = await bridge.call("create_primitive", {"type": primitive_type, "x": x, "y": y, "z": z})
    primitive_path = primitive_result["path"]

    if parent_path:
        await bridge.call("reparent_gameobject", {"path": primitive_path, "newParentPath": parent_path})
        primitive_path = f"{parent_path}/{primitive_path.rsplit('/', 1)[-1]}"

    material_path = f"Materials/MCP/Emissive_{name}.mat"
    await bridge.call("create_material", {"assetPath": material_path, "shaderName": "Standard"})
    await bridge.call("set_material_properties", {
        "assetPath": material_path,
        "keyword": "_EMISSION",
        "keywordEnabled": True,
    })
    await bridge.call("set_material_properties", {
        "assetPath": material_path,
        "propertyName": "_EmissionColor",
        "colorR": r * emission_intensity,
        "colorG": g * emission_intensity,
        "colorB": b * emission_intensity,
        "colorA": 1.0,
    })
    await bridge.call("assign_material", {"path": primitive_path, "materialAssetPath": material_path})

    light_path = None
    if add_point_light:
        light_result = await bridge.call("create_light", {
            "type": "Point",
            "name": f"{name}Light",
            "parentPath": primitive_path,
        })
        light_path = light_result["path"]
        await bridge.call("set_light_properties", {
            "path": light_path,
            "colorR": r,
            "colorG": g,
            "colorB": b,
            "intensity": args.get("lightIntensity", 1.0),
            "range": args.get("lightRange", 5.0),
        })

    return {"path": primitive_path, "materialAssetPath": material_path, "lightPath": light_path}


# ---------------------------------------------------------------------------
# add_camera_shake -- wires up a Cinemachine impulse source/listener pair.
# A plain add_component call is correct here (unlike vcam body/aim, impulse
# source/listener are standalone components, not part of the hidden pipeline
# child Cinemachine manages itself) -- see trigger_camera_impulse to fire it.
# ---------------------------------------------------------------------------


@workflow(
    "add_camera_shake",
    "Wires up Cinemachine camera shake: adds a CinemachineImpulseSource to the given GameObject (the shake's origin "
    "point) and, if not already present, a CinemachineImpulseListener on the Cinemachine Brain camera so it actually "
    "reacts to impulses. Call trigger_camera_impulse afterward to actually fire a shake. Requires the Cinemachine "
    "package (com.unity.cinemachine); fails clearly if it isn't installed.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to add the impulse source to (its position is the shake's origin)."},
            "listenerPath": {"type": "string", "description": "Hierarchy path of the GameObject to add the impulse listener to -- normally wherever the CinemachineBrain lives (usually the Main Camera). Omit to skip adding a listener."},
        },
        "required": ["path"],
    },
    group="cameras",
)
async def _add_camera_shake(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    listener_path = args.get("listenerPath")

    await bridge.call("add_component", {"path": path, "typeName": "Cinemachine.CinemachineImpulseSource"})

    listener_added = False
    if listener_path:
        await bridge.call("add_component", {"path": listener_path, "typeName": "Cinemachine.CinemachineImpulseListener"})
        listener_added = True

    return {"path": path, "listenerPath": listener_path, "listenerAdded": listener_added}


# ---------------------------------------------------------------------------
# add_head_bob -- attaches a small reusable first-person head-bob driver,
# scaffolded the same idempotent way as MCPTriggerRelay/MCPFlickerLight.
# ---------------------------------------------------------------------------

_HEAD_BOB_PATH = "Scripts/MCP/MCPHeadBob.cs"
_HEAD_BOB_CONTENT = """using UnityEngine;

public class MCPHeadBob : MonoBehaviour
{
    public float bobFrequency = 8f;
    public float bobAmplitude = 0.05f;
    public float idleAmplitude = 0.01f;

    private Vector3 _basePosition;
    private CharacterController _controller;
    private Rigidbody _rigidbody;

    private void Awake()
    {
        _basePosition = transform.localPosition;
        _controller = GetComponentInParent<CharacterController>();
        _rigidbody = GetComponentInParent<Rigidbody>();
    }

    private void Update()
    {
        float speed = 0f;
        if (_controller != null)
        {
            var v = _controller.velocity;
            speed = new Vector3(v.x, 0f, v.z).magnitude;
        }
        else if (_rigidbody != null)
        {
            var v = _rigidbody.linearVelocity;
            speed = new Vector3(v.x, 0f, v.z).magnitude;
        }

        float amplitude = speed > 0.1f ? bobAmplitude : idleAmplitude;
        float bob = Mathf.Sin(Time.time * bobFrequency) * amplitude;
        transform.localPosition = _basePosition + new Vector3(0f, bob, 0f);
    }
}
"""


async def _scaffold_head_bob_script(bridge: UnityBridgeClient) -> bool:
    """Returns True if the file was just created (caller should wait for compile), False if it already existed."""
    try:
        await bridge.call("create_script", {"path": _HEAD_BOB_PATH, "template": "PlainClass"})
    except BridgeError as e:
        if "already exists" in str(e):
            return False
        raise
    await bridge.call("update_script", {"path": _HEAD_BOB_PATH, "content": _HEAD_BOB_CONTENT})
    return True


@workflow(
    "add_head_bob",
    "Attaches a small 'MCPHeadBob' component that bobs a first-person camera's local position while its parent is "
    "moving (reads a CharacterController or Rigidbody on a parent, falling back to a subtle idle bob if neither is "
    "found -- works standalone before a full FPS controller exists, and picks up real movement once one does). "
    "Scaffolds the driver script into Assets/Scripts/MCP/ the first time it's needed; safe to call repeatedly.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the camera GameObject to bob."},
            "bobFrequency": {"type": "number", "description": "How fast the bob cycles while moving. Defaults to 8."},
            "bobAmplitude": {"type": "number", "description": "Vertical bob height while moving. Defaults to 0.05."},
            "idleAmplitude": {"type": "number", "description": "Subtle vertical bob height while not moving. Defaults to 0.01."},
        },
        "required": ["path"],
    },
    group="cameras",
)
async def _add_head_bob(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]

    created = await _scaffold_head_bob_script(bridge)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPHeadBob"})

    field_names, values = [], []
    for key in ("bobFrequency", "bobAmplitude", "idleAmplitude"):
        if key in args:
            field_names.append(key)
            values.append(str(args[key]))
    if field_names:
        await bridge.call("set_component_properties_batch", {
            "path": path,
            "typeName": "MCPHeadBob",
            "fieldNames": field_names,
            "values": values,
        })

    return {"path": path, "relayScriptCreated": created}


# ---------------------------------------------------------------------------
# create_render_texture_camera -- a camera rendering into a RenderTexture,
# for CCTV/monitor/portal-style horror setups.
# ---------------------------------------------------------------------------


@workflow(
    "create_render_texture_camera",
    "Creates a Camera that renders into a new RenderTexture asset instead of the screen -- for CCTV monitors, "
    "security-camera props, minimaps, or portal-style effects. Optionally assigns the RenderTexture onto an "
    "existing material's main texture so it can be displayed on a screen prop immediately.",
    {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "Name for the new camera GameObject, and its RenderTexture asset."},
            "parentPath": {"type": "string", "description": "Hierarchy path to parent the new camera under. Omit for scene root."},
            "width": {"type": "number", "description": "RenderTexture width in pixels. Defaults to 1024."},
            "height": {"type": "number", "description": "RenderTexture height in pixels. Defaults to 1024."},
            "targetMaterialPath": {"type": "string", "description": "Path relative to Assets/ of an existing material to display the feed on (sets its _MainTex). Omit to skip."},
        },
        "required": ["name"],
    },
    group="cameras",
)
async def _create_render_texture_camera(bridge: UnityBridgeClient, args: dict) -> Any:
    name = args["name"]
    parent_path = args.get("parentPath")
    width = args.get("width", 1024)
    height = args.get("height", 1024)
    target_material_path = args.get("targetMaterialPath")

    camera_args = {"name": name}
    if parent_path:
        camera_args["parentPath"] = parent_path
    camera_result = await bridge.call("create_camera", camera_args)
    camera_path = camera_result["path"]

    render_texture_path = f"Textures/MCP/{name}.renderTexture"
    await bridge.call("create_render_texture", {"assetPath": render_texture_path, "width": width, "height": height})

    await bridge.call("wire_object_reference", {
        "path": camera_path,
        "typeName": "UnityEngine.Camera",
        "fieldName": "targetTexture",
        "targetAssetPath": render_texture_path,
    })

    if target_material_path:
        await bridge.call("set_material_properties", {
            "assetPath": target_material_path,
            "propertyName": "_MainTex",
            "textureAssetPath": render_texture_path,
        })

    return {"path": camera_path, "renderTexturePath": render_texture_path}


# ---------------------------------------------------------------------------
# FPS Character Controller -- a real CharacterController-based movement core
# (MCPFPSController) plus separate, decoupled scripts for concerns that don't
# have to live in the same Update() loop: look, footsteps, interaction,
# stamina, flashlight, lean. Each is scaffolded the same idempotent way as
# MCPTriggerRelay/MCPFlickerLight. Movement/look inputs (moveInput,
# lookInput, jumpRequested) are public fields meant to be driven externally
# (by a real input system, added in a later batch) -- these scripts don't
# read Input themselves, since Unity's Input System is a per-project choice
# (old Input Manager vs. the newer Input System package) this MCP server
# shouldn't force.
# ---------------------------------------------------------------------------

_FPS_CONTROLLER_PATH = "Scripts/MCP/MCPFPSController.cs"
_FPS_CONTROLLER_CONTENT = """using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class MCPFPSController : MonoBehaviour
{
    [Header("Ground Movement")]
    public float walkSpeed = 4f;
    public float acceleration = 10f;
    public float friction = 8f;

    [Header("Sprint")]
    public float sprintSpeed = 7f;
    public bool isSprinting = false;

    [Header("Crouch")]
    public float crouchHeight = 1f;
    public float crouchSpeed = 2.5f;
    public bool standUpClearanceCheck = true;
    public bool isCrouching = false;

    [Header("Jump")]
    public float jumpHeight = 1.2f;
    public float gravity = -20f;
    public float coyoteTime = 0.15f;

    [Header("External input (drive these from an input system)")]
    public Vector2 moveInput;
    public bool jumpRequested;

    private CharacterController _controller;
    private Vector3 _horizontalVelocity;
    private float _verticalVelocity;
    private float _lastGroundedTime;
    private float _standHeight;
    private Vector3 _standCenter;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _standHeight = _controller.height;
        _standCenter = _controller.center;
    }

    private void Update()
    {
        bool grounded = _controller.isGrounded;
        if (grounded) _lastGroundedTime = Time.time;

        float targetSpeed = isCrouching ? crouchSpeed : (isSprinting ? sprintSpeed : walkSpeed);
        Vector3 wishDir = transform.right * moveInput.x + transform.forward * moveInput.y;
        if (wishDir.sqrMagnitude > 1f) wishDir.Normalize();
        Vector3 targetHorizontal = wishDir * targetSpeed;

        float rate = grounded ? acceleration : acceleration * 0.5f;
        _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, targetHorizontal, rate * Time.deltaTime);
        if (wishDir.sqrMagnitude < 0.01f)
            _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, Vector3.zero, friction * Time.deltaTime);

        bool canCoyoteJump = (Time.time - _lastGroundedTime) <= coyoteTime;
        if (jumpRequested && canCoyoteJump)
        {
            _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            _lastGroundedTime = -999f;
        }
        jumpRequested = false;

        if (grounded && _verticalVelocity < 0f) _verticalVelocity = -2f;
        _verticalVelocity += gravity * Time.deltaTime;

        Vector3 motion = (_horizontalVelocity + new Vector3(0f, _verticalVelocity, 0f)) * Time.deltaTime;
        _controller.Move(motion);
    }

    public void SetCrouch(bool crouch)
    {
        if (crouch == isCrouching) return;
        if (!crouch && standUpClearanceCheck && !HasStandUpClearance()) return;

        isCrouching = crouch;
        _controller.height = crouch ? crouchHeight : _standHeight;
        Vector3 center = _standCenter;
        center.y = _controller.height / 2f;
        _controller.center = center;
    }

    private bool HasStandUpClearance()
    {
        Vector3 point1 = transform.position + Vector3.up * _controller.radius;
        Vector3 point2 = transform.position + Vector3.up * (_standHeight - _controller.radius);
        return !Physics.CheckCapsule(point1, point2, _controller.radius * 0.95f, ~0, QueryTriggerInteraction.Ignore);
    }
}
"""


async def _scaffold_script(bridge: UnityBridgeClient, path: str, content: str) -> bool:
    """Shared idempotent scaffold helper: True if just created (caller should wait for compile), False if it already existed."""
    try:
        await bridge.call("create_script", {"path": path, "template": "PlainClass"})
    except BridgeError as e:
        if "already exists" in str(e):
            return False
        raise
    await bridge.call("update_script", {"path": path, "content": content})
    return True


def _batch_fields(args: dict, keys: list[str]) -> tuple[list[str], list[str]]:
    field_names, values = [], []
    for key in keys:
        if key in args:
            field_names.append(key)
            values.append(str(args[key]))
    return field_names, values


async def _apply_field_batch(bridge: UnityBridgeClient, path: str, type_name: str, args: dict, keys: list[str]) -> None:
    field_names, values = _batch_fields(args, keys)
    if field_names:
        await bridge.call("set_component_properties_batch", {
            "path": path,
            "typeName": type_name,
            "fieldNames": field_names,
            "values": values,
        })


@workflow(
    "create_fps_player",
    "Assembles a complete first-person player rig in one call: a GameObject with a CharacterController, a child "
    "camera at eye height, the MCPFPSController movement core (ground movement/sprint/crouch/jump), and MCPMouseLook "
    "wired to it. Use configure_ground_movement/configure_sprint/configure_crouch/configure_jump afterward to tune "
    "specific values, and add_head_look/add_footstep_system/etc. to layer on the optional systems.",
    {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "Name for the player GameObject. Defaults to 'Player'."},
            "x": {"type": "number", "description": "World-space X position. Defaults to 0."},
            "y": {"type": "number", "description": "World-space Y position. Defaults to 0."},
            "z": {"type": "number", "description": "World-space Z position. Defaults to 0."},
            "radius": {"type": "number", "description": "CharacterController radius. Defaults to 0.4."},
            "height": {"type": "number", "description": "CharacterController (standing) height. Defaults to 2."},
            "eyeHeight": {"type": "number", "description": "Local Y position of the camera child, relative to the player's feet. Defaults to 1.7."},
            "mouseSensitivity": {"type": "number", "description": "Mouse look sensitivity. Defaults to 2."},
        },
        "required": [],
    },
    group="fps_controller",
)
async def _create_fps_player(bridge: UnityBridgeClient, args: dict) -> Any:
    name = args.get("name", "Player")
    x, y, z = args.get("x", 0.0), args.get("y", 0.0), args.get("z", 0.0)
    radius = args.get("radius", 0.4)
    height = args.get("height", 2.0)
    eye_height = args.get("eyeHeight", 1.7)

    await bridge.call("create_gameobject", {"name": name})
    await bridge.call("set_transform", {"path": name, "posX": x, "posY": y, "posZ": z})
    await bridge.call("add_character_controller", {"path": name, "radius": radius, "height": height, "centerY": height / 2.0})

    camera_result = await bridge.call("create_camera", {"name": "PlayerCamera", "parentPath": name, "tagAsMainCamera": True})
    camera_path = camera_result["path"]
    await bridge.call("set_transform", {"path": camera_path, "posX": 0.0, "posY": eye_height, "posZ": 0.0})

    controller_created = await _scaffold_script(bridge, _FPS_CONTROLLER_PATH, _FPS_CONTROLLER_CONTENT)
    look_created = await _scaffold_script(bridge, _MOUSE_LOOK_PATH, _MOUSE_LOOK_CONTENT)
    if controller_created or look_created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": name, "typeName": "MCPFPSController"})
    await bridge.call("add_component", {"path": name, "typeName": "MCPMouseLook"})
    await bridge.call("wire_object_reference", {"path": name, "typeName": "MCPMouseLook", "fieldName": "bodyTransform", "targetGameObjectPath": name})
    await bridge.call("wire_object_reference", {"path": name, "typeName": "MCPMouseLook", "fieldName": "cameraTransform", "targetGameObjectPath": camera_path})
    if "mouseSensitivity" in args:
        await _apply_field_batch(bridge, name, "MCPMouseLook", {"sensitivity": args["mouseSensitivity"]}, ["sensitivity"])

    return {"path": name, "cameraPath": camera_path}


@workflow(
    "configure_ground_movement",
    "Tunes MCPFPSController's ground-movement fields (walk speed/acceleration/friction). Adds the component (with "
    "its script scaffolded, if needed) if the target doesn't already have one from create_fps_player.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the player GameObject."},
            "walkSpeed": {"type": "number", "description": "Walking speed. Defaults to 4."},
            "acceleration": {"type": "number", "description": "How fast ground velocity ramps toward the target speed. Defaults to 10."},
            "friction": {"type": "number", "description": "How fast ground velocity decays toward zero with no input. Defaults to 8."},
        },
        "required": ["path"],
    },
    group="fps_controller",
)
async def _configure_ground_movement(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    created = await _scaffold_script(bridge, _FPS_CONTROLLER_PATH, _FPS_CONTROLLER_CONTENT)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPFPSController"})
    await _apply_field_batch(bridge, path, "MCPFPSController", args, ["walkSpeed", "acceleration", "friction"])
    return {"path": path}


@workflow(
    "configure_sprint",
    "Tunes MCPFPSController's sprint speed and toggles isSprinting. Adds the component (scaffolding its script if "
    "needed) if the target doesn't already have one.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the player GameObject."},
            "sprintSpeed": {"type": "number", "description": "Sprint speed. Defaults to 7."},
            "isSprinting": {"type": "boolean", "description": "Sets the current sprint state directly. Omit to leave unchanged."},
        },
        "required": ["path"],
    },
    group="fps_controller",
)
async def _configure_sprint(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    created = await _scaffold_script(bridge, _FPS_CONTROLLER_PATH, _FPS_CONTROLLER_CONTENT)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPFPSController"})
    await _apply_field_batch(bridge, path, "MCPFPSController", args, ["sprintSpeed", "isSprinting"])
    return {"path": path}


@workflow(
    "configure_crouch",
    "Tunes MCPFPSController's crouch height/speed and whether standing back up requires a clearance check (a real "
    "capsule overlap test against the standing height, so the player can't stand up inside a low ceiling). Adds the "
    "component (scaffolding its script if needed) if the target doesn't already have one.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the player GameObject."},
            "crouchHeight": {"type": "number", "description": "CharacterController height while crouched. Defaults to 1."},
            "crouchSpeed": {"type": "number", "description": "Movement speed while crouched. Defaults to 2.5."},
            "standUpClearanceCheck": {"type": "boolean", "description": "Whether standing up is blocked by an overhead obstruction. Defaults to true."},
        },
        "required": ["path"],
    },
    group="fps_controller",
)
async def _configure_crouch(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    created = await _scaffold_script(bridge, _FPS_CONTROLLER_PATH, _FPS_CONTROLLER_CONTENT)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPFPSController"})
    await _apply_field_batch(bridge, path, "MCPFPSController", args, ["crouchHeight", "crouchSpeed", "standUpClearanceCheck"])
    return {"path": path}


@workflow(
    "configure_jump",
    "Tunes MCPFPSController's jump height, gravity, and coyote time (the grace window after walking off a ledge "
    "during which a jump still registers). Adds the component (scaffolding its script if needed) if the target "
    "doesn't already have one.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the player GameObject."},
            "jumpHeight": {"type": "number", "description": "Peak jump height in meters. Defaults to 1.2."},
            "gravity": {"type": "number", "description": "Gravity acceleration (negative = downward). Defaults to -20."},
            "coyoteTime": {"type": "number", "description": "Grace window in seconds after leaving the ground where a jump still works. Defaults to 0.15."},
        },
        "required": ["path"],
    },
    group="fps_controller",
)
async def _configure_jump(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    created = await _scaffold_script(bridge, _FPS_CONTROLLER_PATH, _FPS_CONTROLLER_CONTENT)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPFPSController"})
    await _apply_field_batch(bridge, path, "MCPFPSController", args, ["jumpHeight", "gravity", "coyoteTime"])
    return {"path": path}


_MOUSE_LOOK_PATH = "Scripts/MCP/MCPMouseLook.cs"
_MOUSE_LOOK_CONTENT = """using UnityEngine;

public class MCPMouseLook : MonoBehaviour
{
    public Transform bodyTransform;
    public Transform cameraTransform;
    public float sensitivity = 2f;
    public float pitchClampMin = -80f;
    public float pitchClampMax = 80f;

    [Header("External input (drive this from an input system)")]
    public Vector2 lookInput;

    private float _pitch;

    private void Update()
    {
        if (bodyTransform != null)
            bodyTransform.Rotate(Vector3.up * (lookInput.x * sensitivity));

        _pitch = Mathf.Clamp(_pitch - lookInput.y * sensitivity, pitchClampMin, pitchClampMax);
        if (cameraTransform != null)
            cameraTransform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

        lookInput = Vector2.zero;
    }
}
"""


@workflow(
    "add_head_look",
    "Attaches MCPMouseLook (yaws the body, pitches the camera, with a clamp) if not already present -- "
    "create_fps_player adds this automatically, so this is mainly for tuning sensitivity/pitch clamp afterward or "
    "adding look to a rig built by hand.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the player (body) GameObject."},
            "cameraPath": {"type": "string", "description": "Hierarchy path of the camera child to pitch. Required only if MCPMouseLook doesn't already have one wired."},
            "sensitivity": {"type": "number", "description": "Look sensitivity. Defaults to 2."},
            "pitchClampMin": {"type": "number", "description": "Minimum pitch in degrees (looking down). Defaults to -80."},
            "pitchClampMax": {"type": "number", "description": "Maximum pitch in degrees (looking up). Defaults to 80."},
        },
        "required": ["path"],
    },
    group="fps_controller",
)
async def _add_head_look(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    camera_path = args.get("cameraPath")

    created = await _scaffold_script(bridge, _MOUSE_LOOK_PATH, _MOUSE_LOOK_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPMouseLook"})
    await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPMouseLook", "fieldName": "bodyTransform", "targetGameObjectPath": path})
    if camera_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPMouseLook", "fieldName": "cameraTransform", "targetGameObjectPath": camera_path})
    await _apply_field_batch(bridge, path, "MCPMouseLook", args, ["sensitivity", "pitchClampMin", "pitchClampMax"])

    return {"path": path}


_FOOTSTEPS_PATH = "Scripts/MCP/MCPFootsteps.cs"
_FOOTSTEPS_CONTENT = """using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class MCPFootsteps : MonoBehaviour
{
    public CharacterController controller;
    public AudioClip defaultFootstepClip;
    public float stepInterval = 0.5f;
    public float volume = 1f;
    public float minSpeedToStep = 0.5f;

    private AudioSource _audioSource;
    private float _stepTimer;

    private void Awake()
    {
        _audioSource = GetComponent<AudioSource>();
        if (controller == null) controller = GetComponentInParent<CharacterController>();
    }

    private void Update()
    {
        if (controller == null) return;

        Vector3 horizontalVelocity = new Vector3(controller.velocity.x, 0f, controller.velocity.z);
        if (controller.isGrounded && horizontalVelocity.magnitude > minSpeedToStep)
        {
            _stepTimer -= Time.deltaTime;
            if (_stepTimer <= 0f)
            {
                if (defaultFootstepClip != null) _audioSource.PlayOneShot(defaultFootstepClip, volume);
                _stepTimer = stepInterval;
            }
        }
        else
        {
            _stepTimer = 0f;
        }
    }
}
"""


@workflow(
    "add_footstep_system",
    "Attaches MCPFootsteps, which plays a footstep sound at a regular interval while the CharacterController is "
    "grounded and moving. Adds an AudioSource automatically if missing. Only a single default clip for now (no "
    "per-surface detection yet) -- pass footstepClipPath once a clip asset exists.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the player GameObject (or a child of it -- the CharacterController is found via GetComponentInParent if not on the same object)."},
            "footstepClipPath": {"type": "string", "description": "Path relative to Assets/ of the AudioClip to play. Omit to leave unset."},
            "stepInterval": {"type": "number", "description": "Seconds between footstep sounds while moving. Defaults to 0.5."},
            "volume": {"type": "number", "description": "Playback volume (0-1). Defaults to 1."},
        },
        "required": ["path"],
    },
    group="fps_controller",
)
async def _add_footstep_system(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    footstep_clip_path = args.get("footstepClipPath")

    created = await _scaffold_script(bridge, _FOOTSTEPS_PATH, _FOOTSTEPS_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPFootsteps"})
    if footstep_clip_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPFootsteps", "fieldName": "defaultFootstepClip", "targetAssetPath": footstep_clip_path})
    await _apply_field_batch(bridge, path, "MCPFootsteps", args, ["stepInterval", "volume"])

    return {"path": path}


_INTERACTABLE_INTERFACE_PATH = "Scripts/MCP/IInteractable.cs"
_INTERACTABLE_INTERFACE_CONTENT = """public interface IInteractable
{
    string GetInteractionPrompt();
    void Interact();
}
"""

_INTERACTION_RAYCASTER_PATH = "Scripts/MCP/MCPInteractionRaycaster.cs"
_INTERACTION_RAYCASTER_CONTENT = """using UnityEngine;
using UnityEngine.Events;

public class MCPInteractionRaycaster : MonoBehaviour
{
    public Transform rayOrigin;
    public float range = 3f;
    public LayerMask layerMask = ~0;

    public UnityEvent<string> onInteractableFound;
    public UnityEvent onInteractableLost;

    private IInteractable _current;

    private void Update()
    {
        Transform origin = rayOrigin != null ? rayOrigin : transform;
        IInteractable hit = null;
        if (Physics.Raycast(origin.position, origin.forward, out RaycastHit hitInfo, range, layerMask))
            hit = hitInfo.collider.GetComponentInParent<IInteractable>();

        if (hit != _current)
        {
            _current = hit;
            if (hit != null) onInteractableFound?.Invoke(hit.GetInteractionPrompt());
            else onInteractableLost?.Invoke();
        }
    }

    public void TryInteract()
    {
        _current?.Interact();
    }
}
"""


@workflow(
    "add_interaction_raycaster",
    "Attaches MCPInteractionRaycaster, which raycasts forward each frame looking for an IInteractable and fires "
    "onInteractableFound/onInteractableLost UnityEvents as the look target changes. Call its public TryInteract() "
    "(e.g. wired to an input action once one exists) to actually trigger Interact(). Scaffolds both the "
    "IInteractable interface and the raycaster script the first time either is needed.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to raycast from -- typically the player camera."},
            "range": {"type": "number", "description": "Maximum interaction distance in meters. Defaults to 3."},
        },
        "required": ["path"],
    },
    group="fps_controller",
)
async def _add_interaction_raycaster(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]

    interface_created = await _scaffold_script(bridge, _INTERACTABLE_INTERFACE_PATH, _INTERACTABLE_INTERFACE_CONTENT)
    raycaster_created = await _scaffold_script(bridge, _INTERACTION_RAYCASTER_PATH, _INTERACTION_RAYCASTER_CONTENT)
    if interface_created or raycaster_created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPInteractionRaycaster"})
    await _apply_field_batch(bridge, path, "MCPInteractionRaycaster", args, ["range"])

    return {"path": path}


_STAMINA_PATH = "Scripts/MCP/MCPStamina.cs"
_STAMINA_CONTENT = """using UnityEngine;
using UnityEngine.Events;

public class MCPStamina : MonoBehaviour
{
    public float maxStamina = 100f;
    public float currentStamina = 100f;
    public float drainRate = 20f;
    public float regenRate = 10f;
    public float regenDelay = 1f;

    public bool isDraining;

    public UnityEvent onDepleted;
    public UnityEvent onFullyRegenerated;

    private float _regenTimer;
    private bool _wasDepleted;

    private void Update()
    {
        if (isDraining && currentStamina > 0f)
        {
            currentStamina = Mathf.Max(0f, currentStamina - drainRate * Time.deltaTime);
            _regenTimer = regenDelay;
            if (currentStamina <= 0f && !_wasDepleted)
            {
                _wasDepleted = true;
                onDepleted?.Invoke();
            }
        }
        else
        {
            if (_regenTimer > 0f)
            {
                _regenTimer -= Time.deltaTime;
            }
            else if (currentStamina < maxStamina)
            {
                currentStamina = Mathf.Min(maxStamina, currentStamina + regenRate * Time.deltaTime);
                if (currentStamina >= maxStamina && _wasDepleted)
                {
                    _wasDepleted = false;
                    onFullyRegenerated?.Invoke();
                }
            }
        }
    }
}
"""


@workflow(
    "add_stamina_system",
    "Attaches MCPStamina, a general-purpose drain/regen resource for sprint and breath-holding: set isDraining "
    "true while the drain should apply (e.g. while sprinting), it regenerates automatically after regenDelay "
    "seconds of not draining, and fires onDepleted/onFullyRegenerated UnityEvents at the extremes.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the player GameObject."},
            "maxStamina": {"type": "number", "description": "Maximum stamina. Defaults to 100."},
            "drainRate": {"type": "number", "description": "Stamina drained per second while isDraining. Defaults to 20."},
            "regenRate": {"type": "number", "description": "Stamina regenerated per second once regenDelay has elapsed. Defaults to 10."},
            "regenDelay": {"type": "number", "description": "Seconds after draining stops before regen begins. Defaults to 1."},
        },
        "required": ["path"],
    },
    group="fps_controller",
)
async def _add_stamina_system(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]

    created = await _scaffold_script(bridge, _STAMINA_PATH, _STAMINA_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPStamina"})
    await _apply_field_batch(bridge, path, "MCPStamina", args, ["maxStamina", "drainRate", "regenRate", "regenDelay"])

    return {"path": path}


_FLASHLIGHT_PATH = "Scripts/MCP/MCPFlashlight.cs"
_FLASHLIGHT_CONTENT = """using UnityEngine;

public class MCPFlashlight : MonoBehaviour
{
    public Light spotLight;
    public bool isOn = false;
    public float batteryCapacity = 100f;
    public float currentBattery = 100f;
    public float drainRate = 5f;
    public float lowBatteryThreshold = 20f;
    public float lowBatteryFlickerSpeed = 15f;

    private void Awake()
    {
        if (spotLight == null) spotLight = GetComponentInChildren<Light>();
        if (spotLight != null) spotLight.enabled = isOn;
    }

    public void Toggle()
    {
        if (!isOn && currentBattery <= 0f) return;
        isOn = !isOn;
        if (spotLight != null) spotLight.enabled = isOn;
    }

    private void Update()
    {
        if (!isOn || spotLight == null) return;

        if (currentBattery > 0f)
        {
            currentBattery = Mathf.Max(0f, currentBattery - drainRate * Time.deltaTime);
            if (currentBattery <= 0f)
            {
                isOn = false;
                spotLight.enabled = false;
                return;
            }
        }

        if (currentBattery <= lowBatteryThreshold)
        {
            float t = Mathf.PerlinNoise(Time.time * lowBatteryFlickerSpeed, 0f);
            spotLight.enabled = t > 0.15f;
        }
    }
}
"""


@workflow(
    "add_flashlight",
    "Creates a child Spot light and attaches MCPFlashlight -- a toggleable flashlight with battery drain and a "
    "low-battery flicker, a horror-game staple. Call the component's Toggle() (e.g. wired to an input action) to "
    "turn it on/off.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to attach the flashlight to -- typically the player camera."},
            "range": {"type": "number", "description": "Spot light range in meters. Defaults to 10."},
            "angle": {"type": "number", "description": "Spot light cone angle in degrees. Defaults to 45."},
            "intensity": {"type": "number", "description": "Spot light intensity. Defaults to 3."},
            "batteryCapacity": {"type": "number", "description": "Maximum battery. Defaults to 100."},
            "drainRate": {"type": "number", "description": "Battery drained per second while on. Defaults to 5."},
        },
        "required": ["path"],
    },
    group="fps_controller",
)
async def _add_flashlight(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    light_range = args.get("range", 10.0)
    light_angle = args.get("angle", 45.0)
    light_intensity = args.get("intensity", 3.0)

    light_result = await bridge.call("create_light", {"type": "Spot", "name": "FlashlightBeam", "parentPath": path})
    light_path = light_result["path"]
    await bridge.call("set_light_properties", {"path": light_path, "range": light_range, "spotAngle": light_angle, "intensity": light_intensity})

    created = await _scaffold_script(bridge, _FLASHLIGHT_PATH, _FLASHLIGHT_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPFlashlight"})
    await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPFlashlight", "fieldName": "spotLight", "targetGameObjectPath": light_path})
    await _apply_field_batch(bridge, path, "MCPFlashlight", args, ["batteryCapacity", "drainRate"])

    return {"path": path, "lightPath": light_path}


_LEAN_PATH = "Scripts/MCP/MCPLean.cs"
_LEAN_CONTENT = """using UnityEngine;

public class MCPLean : MonoBehaviour
{
    public float leanAngle = 15f;
    public float leanDistance = 0.5f;
    public float leanSpeed = 8f;

    // -1 = left, 0 = none, 1 = right
    public int leanDirection = 0;

    private Vector3 _basePosition;
    private Quaternion _baseRotation;
    private bool _initialized;

    private void Awake()
    {
        _basePosition = transform.localPosition;
        _baseRotation = transform.localRotation;
        _initialized = true;
    }

    public void LeanLeft() => leanDirection = -1;
    public void LeanRight() => leanDirection = 1;
    public void LeanNone() => leanDirection = 0;

    private void Update()
    {
        if (!_initialized) return;

        Vector3 targetPos = _basePosition + Vector3.right * (leanDirection * leanDistance);
        Quaternion targetRot = _baseRotation * Quaternion.Euler(0f, 0f, -leanDirection * leanAngle);

        transform.localPosition = Vector3.Lerp(transform.localPosition, targetPos, leanSpeed * Time.deltaTime);
        transform.localRotation = Quaternion.Slerp(transform.localRotation, targetRot, leanSpeed * Time.deltaTime);
    }
}
"""


@workflow(
    "add_lean_system",
    "Attaches MCPLean to a camera (or camera parent) for peek-leaning around corners: call LeanLeft()/LeanRight()/"
    "LeanNone() (e.g. wired to input actions) and it smoothly offsets and tilts the transform toward that lean.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to lean -- typically the player camera."},
            "leanAngle": {"type": "number", "description": "Tilt angle in degrees at full lean. Defaults to 15."},
            "leanDistance": {"type": "number", "description": "Sideways offset in meters at full lean. Defaults to 0.5."},
            "leanSpeed": {"type": "number", "description": "How quickly the lean transitions. Defaults to 8."},
        },
        "required": ["path"],
    },
    group="fps_controller",
)
async def _add_lean_system(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]

    created = await _scaffold_script(bridge, _LEAN_PATH, _LEAN_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPLean"})
    await _apply_field_batch(bridge, path, "MCPLean", args, ["leanAngle", "leanDistance", "leanSpeed"])

    return {"path": path}


# ---------------------------------------------------------------------------
# Weapons & Combat -- two shared foundation scripts (IDamageable, MCPHitReaction)
# that every damage-dealing/damage-receiving script below depends on, scaffolded
# defensively by EACH composite that needs them (the same "scaffold every
# dependency, in order, every time" pattern add_interaction_raycaster uses for
# IInteractable + MCPInteractionRaycaster) so it never matters which of these
# composites gets called first.
# ---------------------------------------------------------------------------

_IDAMAGEABLE_PATH = "Scripts/MCP/IDamageable.cs"
_IDAMAGEABLE_CONTENT = """using UnityEngine;

public interface IDamageable
{
    void TakeDamage(float amount, Vector3 hitPoint, Vector3 hitNormal);
}
"""

_HIT_REACTION_PATH = "Scripts/MCP/MCPHitReaction.cs"
_HIT_REACTION_CONTENT = """using UnityEngine;

public class MCPHitReaction : MonoBehaviour
{
    public GameObject impactPrefab;
    public AudioClip impactSound;
    [Range(0f, 1f)] public float impactVolume = 1f;

    public void PlayReaction(Vector3 point, Vector3 normal)
    {
        if (impactPrefab != null)
            Object.Instantiate(impactPrefab, point, Quaternion.LookRotation(normal));
        if (impactSound != null)
            AudioSource.PlayClipAtPoint(impactSound, point, impactVolume);
    }
}
"""


async def _scaffold_damage_foundation(bridge: UnityBridgeClient) -> bool:
    """Scaffolds IDamageable + MCPHitReaction if either is missing. Returns True if anything was just created."""
    created_a = await _scaffold_script(bridge, _IDAMAGEABLE_PATH, _IDAMAGEABLE_CONTENT)
    created_b = await _scaffold_script(bridge, _HIT_REACTION_PATH, _HIT_REACTION_CONTENT)
    return created_a or created_b


@workflow(
    "create_weapon",
    "Scaffolds a weapon GameObject rig: the weapon itself (optionally with a primitive placeholder model child) "
    "plus an empty 'Muzzle' child transform for hitscan/projectile origins and muzzle-flash placement. Does not "
    "create a separate weapon-data asset -- configure_hitscan/configure_projectile/add_ammo_system/etc. configure "
    "the runtime components directly, so a parallel data layer they wouldn't read from would just be dead weight.",
    {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "Name for the weapon GameObject."},
            "parentPath": {"type": "string", "description": "Hierarchy path to parent the weapon under -- typically the player camera, for a first-person view model. Omit for scene root."},
            "modelPrimitive": {
                "type": "string",
                "enum": ["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"],
                "description": "If given, creates a primitive as a visual placeholder child named 'Model'. Omit to create no visual.",
            },
            "muzzleForwardOffset": {"type": "number", "description": "Local Z offset of the Muzzle child, in front of the weapon. Defaults to 0.5."},
        },
        "required": ["name"],
    },
    group="weapons",
)
async def _create_weapon(bridge: UnityBridgeClient, args: dict) -> Any:
    name = args["name"]
    parent_path = args.get("parentPath")
    model_primitive = args.get("modelPrimitive")
    muzzle_offset = args.get("muzzleForwardOffset", 0.5)

    weapon_args = {"name": name}
    if parent_path:
        weapon_args["parentPath"] = parent_path
    await bridge.call("create_gameobject", weapon_args)
    weapon_path = f"{parent_path}/{name}" if parent_path else name

    model_path = None
    if model_primitive:
        primitive_result = await bridge.call("create_primitive", {"type": model_primitive})
        primitive_path = primitive_result["path"]
        await bridge.call("reparent_gameobject", {"path": primitive_path, "newParentPath": weapon_path})
        model_path = f"{weapon_path}/{primitive_path.rsplit('/', 1)[-1]}"
        await bridge.call("rename_gameobject", {"path": model_path, "newName": "Model"})
        model_path = f"{weapon_path}/Model"

    await bridge.call("create_gameobject", {"name": "Muzzle", "parentPath": weapon_path})
    muzzle_path = f"{weapon_path}/Muzzle"
    await bridge.call("set_transform", {"path": muzzle_path, "posZ": muzzle_offset})

    return {"path": weapon_path, "muzzlePath": muzzle_path, "modelPath": model_path}


_HITSCAN_WEAPON_PATH = "Scripts/MCP/MCPHitscanWeapon.cs"
_HITSCAN_WEAPON_CONTENT = """using UnityEngine;

public class MCPHitscanWeapon : MonoBehaviour
{
    public Transform muzzle;
    public float damage = 20f;
    public float range = 100f;
    public float spread = 0f;
    public float fireRate = 8f;
    public LayerMask hitMask = ~0;

    private float _nextFireTime;

    public bool TryFire()
    {
        if (Time.time < _nextFireTime) return false;
        _nextFireTime = Time.time + 1f / Mathf.Max(0.01f, fireRate);

        Transform origin = muzzle != null ? muzzle : transform;
        Vector3 direction = origin.forward;
        if (spread > 0f)
            direction = Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0f) * direction;

        if (Physics.Raycast(origin.position, direction, out RaycastHit hit, range, hitMask))
        {
            var damageable = hit.collider.GetComponentInParent<IDamageable>();
            damageable?.TakeDamage(damage, hit.point, hit.normal);

            var reaction = hit.collider.GetComponentInParent<MCPHitReaction>();
            if (reaction != null) reaction.PlayReaction(hit.point, hit.normal);
        }

        return true;
    }
}
"""


@workflow(
    "configure_hitscan",
    "Attaches/tunes MCPHitscanWeapon: an instant raycast-fire weapon with damage, range, spread, and fire rate. "
    "Call its public TryFire() (e.g. wired to an input action) to actually fire. Scaffolds IDamageable and "
    "MCPHitReaction as shared dependencies if either is missing.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the weapon GameObject."},
            "muzzlePath": {"type": "string", "description": "Hierarchy path of a Muzzle transform to fire from (see create_weapon). Omit to fire from the weapon's own transform."},
            "damage": {"type": "number", "description": "Damage per hit. Defaults to 20."},
            "range": {"type": "number", "description": "Maximum hit distance in meters. Defaults to 100."},
            "spread": {"type": "number", "description": "Random cone spread in degrees (0 = perfectly accurate). Defaults to 0."},
            "fireRate": {"type": "number", "description": "Shots per second. Defaults to 8."},
        },
        "required": ["path"],
    },
    group="weapons",
)
async def _configure_hitscan(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    muzzle_path = args.get("muzzlePath")

    foundation_created = await _scaffold_damage_foundation(bridge)
    script_created = await _scaffold_script(bridge, _HITSCAN_WEAPON_PATH, _HITSCAN_WEAPON_CONTENT)
    if foundation_created or script_created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPHitscanWeapon"})
    if muzzle_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPHitscanWeapon", "fieldName": "muzzle", "targetGameObjectPath": muzzle_path})
    await _apply_field_batch(bridge, path, "MCPHitscanWeapon", args, ["damage", "range", "spread", "fireRate"])

    return {"path": path}


_PROJECTILE_PATH = "Scripts/MCP/MCPProjectile.cs"
_PROJECTILE_CONTENT = """using UnityEngine;

public class MCPProjectile : MonoBehaviour
{
    public float speed = 20f;
    public float gravity = -9.81f;
    public float damage = 25f;
    public float lifetime = 5f;

    private Vector3 _velocity;

    private void Start()
    {
        _velocity = transform.forward * speed;
        Destroy(gameObject, lifetime);
    }

    private void Update()
    {
        _velocity.y += gravity * Time.deltaTime;
        transform.position += _velocity * Time.deltaTime;
        if (_velocity.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(_velocity.normalized);
    }

    private void OnTriggerEnter(Collider other)
    {
        var damageable = other.GetComponentInParent<IDamageable>();
        damageable?.TakeDamage(damage, transform.position, -transform.forward);

        var reaction = other.GetComponentInParent<MCPHitReaction>();
        if (reaction != null) reaction.PlayReaction(transform.position, -transform.forward);

        Destroy(gameObject);
    }
}
"""

_PROJECTILE_WEAPON_PATH = "Scripts/MCP/MCPProjectileWeapon.cs"
_PROJECTILE_WEAPON_CONTENT = """using UnityEngine;

public class MCPProjectileWeapon : MonoBehaviour
{
    public Transform muzzle;
    public GameObject projectilePrefab;
    public float speed = 20f;
    public float gravity = -9.81f;
    public float damage = 25f;
    public float fireRate = 2f;

    private float _nextFireTime;

    public bool TryFire()
    {
        if (Time.time < _nextFireTime || projectilePrefab == null) return false;
        _nextFireTime = Time.time + 1f / Mathf.Max(0.01f, fireRate);

        Transform origin = muzzle != null ? muzzle : transform;
        GameObject instance = Object.Instantiate(projectilePrefab, origin.position, origin.rotation);
        var projectile = instance.GetComponent<MCPProjectile>();
        if (projectile != null)
        {
            projectile.speed = speed;
            projectile.gravity = gravity;
            projectile.damage = damage;
        }

        return true;
    }
}
"""


@workflow(
    "configure_projectile",
    "Attaches/tunes MCPProjectileWeapon, which spawns a projectile prefab with a given speed/gravity/damage. If "
    "projectilePrefabPath isn't given, creates and saves a minimal default projectile prefab (a small trigger "
    "sphere with MCPProjectile attached, no visible mesh unless one is added afterward). Scaffolds IDamageable and "
    "MCPHitReaction as shared dependencies if either is missing. Call the weapon's public TryFire() to actually fire.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the weapon GameObject."},
            "muzzlePath": {"type": "string", "description": "Hierarchy path of a Muzzle transform to fire from. Omit to fire from the weapon's own transform."},
            "projectilePrefabPath": {"type": "string", "description": "Path relative to Assets/ of an existing prefab with MCPProjectile on it. Omit to auto-create a minimal default at 'Prefabs/MCP/DefaultProjectile.prefab'."},
            "speed": {"type": "number", "description": "Projectile launch speed. Defaults to 20."},
            "gravity": {"type": "number", "description": "Projectile gravity acceleration (negative = downward). Defaults to -9.81."},
            "damage": {"type": "number", "description": "Damage dealt on impact. Defaults to 25."},
            "fireRate": {"type": "number", "description": "Shots per second. Defaults to 2."},
        },
        "required": ["path"],
    },
    group="weapons",
)
async def _configure_projectile(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    muzzle_path = args.get("muzzlePath")
    projectile_prefab_path = args.get("projectilePrefabPath")

    foundation_created = await _scaffold_damage_foundation(bridge)
    projectile_script_created = await _scaffold_script(bridge, _PROJECTILE_PATH, _PROJECTILE_CONTENT)
    weapon_script_created = await _scaffold_script(bridge, _PROJECTILE_WEAPON_PATH, _PROJECTILE_WEAPON_CONTENT)
    if foundation_created or projectile_script_created or weapon_script_created:
        await _wait_for_compile(bridge)

    if not projectile_prefab_path:
        temp_result = await bridge.call("create_primitive", {"type": "Sphere"})
        temp_path = temp_result["path"]
        await bridge.call("set_transform", {"path": temp_path, "scaleX": 0.15, "scaleY": 0.15, "scaleZ": 0.15})
        await bridge.call("add_collider", {"path": temp_path, "type": "Sphere", "isTrigger": True})
        await bridge.call("add_component", {"path": temp_path, "typeName": "MCPProjectile"})
        projectile_prefab_path = "Prefabs/MCP/DefaultProjectile.prefab"
        await bridge.call("create_prefab", {"gameObjectPath": temp_path, "assetPath": projectile_prefab_path})
        await bridge.call("delete_gameobject", {"path": temp_path, "confirm": True})

    await bridge.call("add_component", {"path": path, "typeName": "MCPProjectileWeapon"})
    if muzzle_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPProjectileWeapon", "fieldName": "muzzle", "targetGameObjectPath": muzzle_path})
    await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPProjectileWeapon", "fieldName": "projectilePrefab", "targetAssetPath": projectile_prefab_path})
    await _apply_field_batch(bridge, path, "MCPProjectileWeapon", args, ["speed", "gravity", "damage", "fireRate"])

    return {"path": path, "projectilePrefabPath": projectile_prefab_path}


_AMMO_SYSTEM_PATH = "Scripts/MCP/MCPAmmoSystem.cs"
_AMMO_SYSTEM_CONTENT = """using UnityEngine;
using UnityEngine.Events;

public class MCPAmmoSystem : MonoBehaviour
{
    public int magazineSize = 30;
    public int currentMagazine = 30;
    public int reserveAmmo = 90;
    public float reloadTime = 1.5f;
    public bool isReloading;

    public UnityEvent onReloadStarted;
    public UnityEvent onReloadFinished;
    public UnityEvent onEmpty;

    private float _reloadEndTime;

    public bool TryConsumeRound()
    {
        if (isReloading || currentMagazine <= 0) return false;
        currentMagazine--;
        if (currentMagazine <= 0) onEmpty?.Invoke();
        return true;
    }

    public void StartReload()
    {
        if (isReloading || currentMagazine >= magazineSize || reserveAmmo <= 0) return;
        isReloading = true;
        _reloadEndTime = Time.time + reloadTime;
        onReloadStarted?.Invoke();
    }

    private void Update()
    {
        if (!isReloading || Time.time < _reloadEndTime) return;

        int needed = magazineSize - currentMagazine;
        int loaded = Mathf.Min(needed, reserveAmmo);
        currentMagazine += loaded;
        reserveAmmo -= loaded;
        isReloading = false;
        onReloadFinished?.Invoke();
    }
}
"""


@workflow(
    "add_ammo_system",
    "Attaches/tunes MCPAmmoSystem: magazine size, reserve ammo, and reload timing, with onReloadStarted/"
    "onReloadFinished/onEmpty UnityEvents. Call TryConsumeRound() per shot and StartReload() to reload.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the weapon GameObject."},
            "magazineSize": {"type": "number", "description": "Rounds per magazine. Defaults to 30."},
            "reserveAmmo": {"type": "number", "description": "Starting reserve ammo. Defaults to 90."},
            "reloadTime": {"type": "number", "description": "Seconds a reload takes. Defaults to 1.5."},
        },
        "required": ["path"],
    },
    group="weapons",
)
async def _add_ammo_system(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    created = await _scaffold_script(bridge, _AMMO_SYSTEM_PATH, _AMMO_SYSTEM_CONTENT)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPAmmoSystem"})
    await _apply_field_batch(bridge, path, "MCPAmmoSystem", args, ["magazineSize", "reserveAmmo", "reloadTime"])
    return {"path": path}


_RECOIL_PATH = "Scripts/MCP/MCPRecoil.cs"
_RECOIL_CONTENT = """using UnityEngine;

public class MCPRecoil : MonoBehaviour
{
    public float kickPitch = 2f;
    public float kickYaw = 0.5f;
    public float recoverySpeed = 8f;

    private float _currentPitch;
    private float _currentYaw;

    public void Kick()
    {
        _currentPitch += kickPitch;
        _currentYaw += Random.Range(-kickYaw, kickYaw);
    }

    private void Update()
    {
        _currentPitch = Mathf.Lerp(_currentPitch, 0f, recoverySpeed * Time.deltaTime);
        _currentYaw = Mathf.Lerp(_currentYaw, 0f, recoverySpeed * Time.deltaTime);
        transform.localRotation = Quaternion.Euler(-_currentPitch, _currentYaw, 0f);
    }
}
"""


@workflow(
    "add_recoil",
    "Attaches/tunes MCPRecoil, which kicks the weapon's own local rotation on each Kick() call (e.g. wired to a "
    "weapon's fire event) and recovers smoothly over time via recoverySpeed.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the weapon GameObject to rotate."},
            "kickPitch": {"type": "number", "description": "Upward kick per shot, in degrees. Defaults to 2."},
            "kickYaw": {"type": "number", "description": "Random horizontal kick per shot, in degrees. Defaults to 0.5."},
            "recoverySpeed": {"type": "number", "description": "How quickly the kick settles back to neutral. Defaults to 8."},
        },
        "required": ["path"],
    },
    group="weapons",
)
async def _add_recoil(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    created = await _scaffold_script(bridge, _RECOIL_PATH, _RECOIL_CONTENT)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPRecoil"})
    await _apply_field_batch(bridge, path, "MCPRecoil", args, ["kickPitch", "kickYaw", "recoverySpeed"])
    return {"path": path}


_MUZZLE_FLASH_PATH = "Scripts/MCP/MCPMuzzleFlash.cs"
_MUZZLE_FLASH_CONTENT = """using UnityEngine;

[RequireComponent(typeof(Light))]
public class MCPMuzzleFlash : MonoBehaviour
{
    public float intensity = 8f;
    public float duration = 0.05f;

    private Light _light;
    private float _endTime;

    private void Awake()
    {
        _light = GetComponent<Light>();
        _light.enabled = false;
    }

    public void Flash()
    {
        _light.intensity = intensity;
        _light.enabled = true;
        _endTime = Time.time + duration;
    }

    private void Update()
    {
        if (_light.enabled && Time.time >= _endTime)
            _light.enabled = false;
    }
}
"""


@workflow(
    "add_muzzle_flash",
    "Creates a Point light at the muzzle and attaches MCPMuzzleFlash, a brief light 'pop' triggered by calling its "
    "Flash() method (e.g. wired to a weapon's fire event). No particle VFX yet -- that lands with the vfx group.",
    {
        "type": "object",
        "properties": {
            "muzzlePath": {"type": "string", "description": "Hierarchy path to parent the flash light under -- typically the weapon's Muzzle transform from create_weapon."},
            "intensity": {"type": "number", "description": "Light intensity while flashing. Defaults to 8."},
            "duration": {"type": "number", "description": "How long the flash stays lit, in seconds. Defaults to 0.05."},
        },
        "required": ["muzzlePath"],
    },
    group="weapons",
)
async def _add_muzzle_flash(bridge: UnityBridgeClient, args: dict) -> Any:
    muzzle_path = args["muzzlePath"]

    light_result = await bridge.call("create_light", {"type": "Point", "name": "MuzzleFlashLight", "parentPath": muzzle_path})
    light_path = light_result["path"]

    created = await _scaffold_script(bridge, _MUZZLE_FLASH_PATH, _MUZZLE_FLASH_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": light_path, "typeName": "MCPMuzzleFlash"})
    await _apply_field_batch(bridge, light_path, "MCPMuzzleFlash", args, ["intensity", "duration"])

    return {"path": light_path}


_WEAPON_SWAY_PATH = "Scripts/MCP/MCPWeaponSway.cs"
_WEAPON_SWAY_CONTENT = """using UnityEngine;

public class MCPWeaponSway : MonoBehaviour
{
    public float swayAmount = 0.02f;
    public float swaySpeed = 4f;
    public float smoothing = 8f;

    [Header("External input (drive this from look/move input, optional)")]
    public Vector2 lookInput;

    private Vector3 _basePosition;

    private void Awake()
    {
        _basePosition = transform.localPosition;
    }

    private void Update()
    {
        float idleX = Mathf.Sin(Time.time * swaySpeed) * swayAmount;
        float idleY = Mathf.Sin(Time.time * swaySpeed * 2f) * swayAmount * 0.5f;
        float inputX = -lookInput.x * swayAmount;
        float inputY = -lookInput.y * swayAmount;

        Vector3 targetPos = _basePosition + new Vector3(idleX + inputX, idleY + inputY, 0f);
        transform.localPosition = Vector3.Lerp(transform.localPosition, targetPos, smoothing * Time.deltaTime);
    }
}
"""


@workflow(
    "add_weapon_sway",
    "Attaches/tunes MCPWeaponSway: a subtle idle sine-wave sway on the weapon's local position, plus an optional "
    "look-input-driven sway (set its lookInput field from a mouse-look script for movement-reactive sway).",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the weapon GameObject."},
            "swayAmount": {"type": "number", "description": "Sway offset magnitude in local units. Defaults to 0.02."},
            "swaySpeed": {"type": "number", "description": "Idle sway oscillation speed. Defaults to 4."},
        },
        "required": ["path"],
    },
    group="weapons",
)
async def _add_weapon_sway(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    created = await _scaffold_script(bridge, _WEAPON_SWAY_PATH, _WEAPON_SWAY_CONTENT)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPWeaponSway"})
    await _apply_field_batch(bridge, path, "MCPWeaponSway", args, ["swayAmount", "swaySpeed"])
    return {"path": path}


@workflow(
    "add_hit_reaction",
    "Attaches/tunes MCPHitReaction on a hittable target (an enemy, prop, or wall): spawns an impact prefab "
    "(oriented to the hit normal) and/or plays an impact sound wherever it's hit. Hitscan/projectile/melee weapons "
    "look this component up on whatever they hit and call it automatically -- nothing else needs to reference it "
    "directly.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the hittable target GameObject."},
            "impactPrefabPath": {"type": "string", "description": "Path relative to Assets/ of a prefab to spawn on impact (a decal, spark particle, etc). Omit to skip."},
            "impactSoundPath": {"type": "string", "description": "Path relative to Assets/ of an AudioClip to play on impact. Omit to skip."},
            "impactVolume": {"type": "number", "description": "Impact sound volume (0-1). Defaults to 1."},
        },
        "required": ["path"],
    },
    group="weapons",
)
async def _add_hit_reaction(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    impact_prefab_path = args.get("impactPrefabPath")
    impact_sound_path = args.get("impactSoundPath")

    created = await _scaffold_script(bridge, _HIT_REACTION_PATH, _HIT_REACTION_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPHitReaction"})
    if impact_prefab_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPHitReaction", "fieldName": "impactPrefab", "targetAssetPath": impact_prefab_path})
    if impact_sound_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPHitReaction", "fieldName": "impactSound", "targetAssetPath": impact_sound_path})
    await _apply_field_batch(bridge, path, "MCPHitReaction", args, ["impactVolume"])

    return {"path": path}


_MELEE_ATTACK_PATH = "Scripts/MCP/MCPMeleeAttack.cs"
_MELEE_ATTACK_CONTENT = """using UnityEngine;

public class MCPMeleeAttack : MonoBehaviour
{
    public Transform origin;
    public float range = 1.5f;
    public float radius = 0.5f;
    public float damage = 35f;
    public float cooldown = 0.6f;
    public LayerMask hitMask = ~0;

    private float _nextAttackTime;

    public bool TryAttack()
    {
        if (Time.time < _nextAttackTime) return false;
        _nextAttackTime = Time.time + cooldown;

        Transform o = origin != null ? origin : transform;
        if (Physics.SphereCast(o.position, radius, o.forward, out RaycastHit hit, range, hitMask))
        {
            var damageable = hit.collider.GetComponentInParent<IDamageable>();
            damageable?.TakeDamage(damage, hit.point, hit.normal);

            var reaction = hit.collider.GetComponentInParent<MCPHitReaction>();
            if (reaction != null) reaction.PlayReaction(hit.point, hit.normal);
        }

        return true;
    }
}
"""


@workflow(
    "add_melee_attack",
    "Attaches/tunes MCPMeleeAttack: a sphere-cast arc attack with damage and a cooldown. Call its public "
    "TryAttack() (e.g. wired to an input action) to actually swing. Scaffolds IDamageable and MCPHitReaction as "
    "shared dependencies if either is missing.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to attach the melee attack to -- typically the player camera or a weapon."},
            "originPath": {"type": "string", "description": "Hierarchy path of a transform to attack from. Omit to use the attack's own transform."},
            "range": {"type": "number", "description": "Attack reach in meters. Defaults to 1.5."},
            "radius": {"type": "number", "description": "Sphere-cast radius (arc width). Defaults to 0.5."},
            "damage": {"type": "number", "description": "Damage per hit. Defaults to 35."},
            "cooldown": {"type": "number", "description": "Seconds between attacks. Defaults to 0.6."},
        },
        "required": ["path"],
    },
    group="weapons",
)
async def _add_melee_attack(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    origin_path = args.get("originPath")

    foundation_created = await _scaffold_damage_foundation(bridge)
    script_created = await _scaffold_script(bridge, _MELEE_ATTACK_PATH, _MELEE_ATTACK_CONTENT)
    if foundation_created or script_created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPMeleeAttack"})
    if origin_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPMeleeAttack", "fieldName": "origin", "targetGameObjectPath": origin_path})
    await _apply_field_batch(bridge, path, "MCPMeleeAttack", args, ["range", "radius", "damage", "cooldown"])

    return {"path": path}


_HEALTH_PATH = "Scripts/MCP/MCPHealth.cs"
_HEALTH_CONTENT = """using UnityEngine;
using UnityEngine.Events;

public class MCPHealth : MonoBehaviour, IDamageable
{
    public float maxHealth = 100f;
    public float currentHealth = 100f;
    public bool isDead;

    public UnityEvent<float> onDamaged;
    public UnityEvent onDied;

    public void TakeDamage(float amount, Vector3 hitPoint, Vector3 hitNormal)
    {
        if (isDead) return;

        currentHealth = Mathf.Max(0f, currentHealth - amount);
        onDamaged?.Invoke(amount);

        if (currentHealth <= 0f)
        {
            isDead = true;
            onDied?.Invoke();
        }
    }
}
"""

_HIT_ZONE_PATH = "Scripts/MCP/MCPHitZone.cs"
_HIT_ZONE_CONTENT = """using UnityEngine;

public class MCPHitZone : MonoBehaviour, IDamageable
{
    public float damageMultiplier = 1f;
    public MCPHealth health;

    private void Awake()
    {
        if (health == null) health = GetComponentInParent<MCPHealth>();
    }

    public void TakeDamage(float amount, Vector3 hitPoint, Vector3 hitNormal)
    {
        if (health != null) health.TakeDamage(amount * damageMultiplier, hitPoint, hitNormal);
    }
}
"""


@workflow(
    "create_damage_receiver",
    "Attaches MCPHealth (max/current health, onDamaged/onDied UnityEvents, implements IDamageable) to a "
    "GameObject, and optionally MCPHitZone on a child collider (e.g. a head) with its own damage multiplier for "
    "headshots -- weapons that hit the zone collider deal multiplied damage into the same MCPHealth. Scaffolds "
    "IDamageable as a shared dependency if missing.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to give health to."},
            "maxHealth": {"type": "number", "description": "Maximum (and starting) health. Defaults to 100."},
            "headZonePath": {"type": "string", "description": "Hierarchy path of a child collider to mark as a hit zone (e.g. a head). Omit to skip."},
            "headshotMultiplier": {"type": "number", "description": "Damage multiplier for the hit zone. Defaults to 2."},
        },
        "required": ["path"],
    },
    group="weapons",
)
async def _create_damage_receiver(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    head_zone_path = args.get("headZonePath")

    idamageable_created = await _scaffold_script(bridge, _IDAMAGEABLE_PATH, _IDAMAGEABLE_CONTENT)
    health_created = await _scaffold_script(bridge, _HEALTH_PATH, _HEALTH_CONTENT)
    hit_zone_created = False
    if head_zone_path:
        hit_zone_created = await _scaffold_script(bridge, _HIT_ZONE_PATH, _HIT_ZONE_CONTENT)
    if idamageable_created or health_created or hit_zone_created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPHealth"})
    await _apply_field_batch(bridge, path, "MCPHealth", args, ["maxHealth"])

    if head_zone_path:
        await bridge.call("add_component", {"path": head_zone_path, "typeName": "MCPHitZone"})
        await bridge.call("wire_object_reference", {"path": head_zone_path, "typeName": "MCPHitZone", "fieldName": "health", "targetGameObjectPath": path})
        if "headshotMultiplier" in args:
            await bridge.call("set_component_properties_batch", {
                "path": head_zone_path,
                "typeName": "MCPHitZone",
                "fieldNames": ["damageMultiplier"],
                "values": [str(args["headshotMultiplier"])],
            })

    return {"path": path, "headZonePath": head_zone_path}


_WEAPON_SWITCHER_PATH = "Scripts/MCP/MCPWeaponSwitcher.cs"
_WEAPON_SWITCHER_CONTENT = """using UnityEngine;
using UnityEngine.Events;

public class MCPWeaponSwitcher : MonoBehaviour
{
    public int currentIndex = -1;

    public UnityEvent<int> onWeaponEquipped;

    private Transform[] _weapons;

    private void Awake()
    {
        int count = transform.childCount;
        _weapons = new Transform[count];
        for (int i = 0; i < count; i++)
        {
            _weapons[i] = transform.GetChild(i);
            _weapons[i].gameObject.SetActive(false);
        }

        if (count > 0) EquipSlot(0);
    }

    public void EquipSlot(int index)
    {
        if (_weapons == null || index < 0 || index >= _weapons.Length) return;

        if (currentIndex >= 0 && currentIndex < _weapons.Length)
            _weapons[currentIndex].gameObject.SetActive(false);

        currentIndex = index;
        _weapons[currentIndex].gameObject.SetActive(true);
        onWeaponEquipped?.Invoke(currentIndex);
    }

    public void NextWeapon()
    {
        if (_weapons == null || _weapons.Length == 0) return;
        EquipSlot((currentIndex + 1) % _weapons.Length);
    }

    public void PreviousWeapon()
    {
        if (_weapons == null || _weapons.Length == 0) return;
        EquipSlot((currentIndex - 1 + _weapons.Length) % _weapons.Length);
    }
}
"""


@workflow(
    "add_weapon_switching",
    "Attaches MCPWeaponSwitcher to an inventory holder GameObject: each of its DIRECT CHILDREN is treated as a "
    "weapon slot (in hierarchy order), auto-discovered at runtime -- only one is active at a time. If weaponPaths "
    "is given, those existing weapons are reparented under the holder first, in the given order. Call EquipSlot(i)/"
    "NextWeapon()/PreviousWeapon() (e.g. wired to input actions) to switch.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the inventory holder GameObject (its children become weapon slots)."},
            "weaponPaths": {
                "type": "array",
                "items": {"type": "string"},
                "description": "Hierarchy paths of existing weapon GameObjects to reparent under the holder, in slot order. Omit if weapons are already children of path.",
            },
        },
        "required": ["path"],
    },
    group="weapons",
)
async def _add_weapon_switching(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    weapon_paths = args.get("weaponPaths") or []

    for weapon_path in weapon_paths:
        await bridge.call("reparent_gameobject", {"path": weapon_path, "newParentPath": path})

    created = await _scaffold_script(bridge, _WEAPON_SWITCHER_PATH, _WEAPON_SWITCHER_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPWeaponSwitcher"})

    return {"path": path, "weaponCount": len(weapon_paths) if weapon_paths else None}


# ---------------------------------------------------------------------------
# Enemy AI -- a consolidated state-machine "brain" (MCPEnemyBrain: patrol/chase/
# search/attack/stalk share one NavMeshAgent.SetDestination() call per frame and
# one mutually-exclusive state, so -- same reasoning as MCPFPSController in
# batch 10 -- they live in one component rather than several independently-added
# ones) plus decoupled sensors that call into it.
#
# The catalog lists 3 tools here as atomic (add_bt_node, connect_bt_nodes,
# set_blackboard_key) and one composite (scaffold_behavior_tree) that on paper
# duplicate what the EXISTING scaffold_behavior_tree_framework/
# create_behavior_tree/add_behavior_tree_node composites already do (build and
# extend a tree from the GameObject hierarchy) -- rather than ship a second,
# parallel Behavior Tree mechanism with overlapping tools (the exact kind of
# duplication Part A of this project removed), those three are treated as
# already covered. set_blackboard_key is real and new, but ships as a Python
# composite rather than a literal atomic C# tool: a Blackboard has to be a
# scaffolded user-project script (same as every other gameplay script this
# server generates), and the bridge's own compiled C# can't reference a type
# that only exists in the target project's Assembly-CSharp -- exactly why
# fps_controller/weapons went through Python composites + add_component/
# set_component_field instead of new [MCPTool] methods too.
# ---------------------------------------------------------------------------

_BLACKBOARD_PATH = "Scripts/MCP/MCPBlackboard.cs"
_BLACKBOARD_CONTENT = """using UnityEngine;

// A simple shared key-value store for custom Behavior Tree nodes/sensors to read
// and write. Stored as one JSON string (set by the set_blackboard_key MCP tool,
// or by any script at runtime) rather than a Unity-serializable dictionary, since
// Unity has no native Inspector-friendly heterogeneous dictionary type. Read it
// with Newtonsoft.Json (already a project dependency) from custom scripts, e.g.:
//   var obj = Newtonsoft.Json.Linq.JObject.Parse(blackboard.data);
//   float health = (float)obj["targetHealth"];
public class MCPBlackboard : MonoBehaviour
{
    [TextArea] public string data = "{}";
}
"""


@workflow(
    "set_blackboard_key",
    "Sets a key on a GameObject's Blackboard (a simple shared JSON key-value store custom Behavior Tree nodes and "
    "sensors can read), adding the Blackboard component if missing. Values are stored as JSON -- numbers, strings, "
    "and booleans are supported directly; for GameObject/Vector3 references, store a path or coordinates as a "
    "plain value and resolve them yourself in the reading script.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject with (or to receive) the Blackboard."},
            "key": {"type": "string", "description": "Key name."},
            "value": {"description": "Any JSON-serializable value: a number, string, or boolean."},
        },
        "required": ["path", "key", "value"],
    },
    group="behavior_tree",
)
async def _set_blackboard_key(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    key = args["key"]
    value = args["value"]

    created = await _scaffold_script(bridge, _BLACKBOARD_PATH, _BLACKBOARD_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPBlackboard"})

    current_data = {}
    try:
        read_result = await bridge.call("get_component_field", {"path": path, "typeName": "MCPBlackboard", "fieldName": "data"})
        raw = read_result.get("value") if isinstance(read_result, dict) else None
        if raw:
            current_data = json.loads(raw)
    except (BridgeError, ValueError, TypeError):
        current_data = {}

    current_data[key] = value
    await bridge.call("set_component_field", {"path": path, "typeName": "MCPBlackboard", "fieldName": "data", "value": json.dumps(current_data)})

    return {"path": path, "key": key, "value": value}


_ENEMY_BRAIN_PATH = "Scripts/MCP/MCPEnemyBrain.cs"
_ENEMY_BRAIN_CONTENT = """using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

public enum MCPEnemyState { Idle, Patrol, Chase, Search, Attack, Stalk }

[RequireComponent(typeof(NavMeshAgent))]
public class MCPEnemyBrain : MonoBehaviour
{
    public MCPEnemyState currentState = MCPEnemyState.Idle;
    public Transform target;
    public Vector3 lastKnownTargetPosition;

    [Header("Chase")]
    public float chaseSpeed = 5f;

    [Header("Attack")]
    public float attackRange = 2f;
    public float telegraphDuration = 0.5f;
    public UnityEvent onAttackTelegraphed;

    [Header("Search")]
    public float searchDuration = 5f;

    [Header("Patrol -- point to a holder GameObject; its direct children become waypoints, in order")]
    public Transform patrolRouteParent;
    public float patrolSpeedMultiplier = 0.5f;

    [Header("Stalker (retreat when close/seen, approach when far/unseen -- horror signature)")]
    public bool useStalkerBehavior;
    public float stalkerRetreatDistance = 15f;
    public float stalkerApproachDistance = 25f;

    private NavMeshAgent _agent;
    private Transform[] _patrolPoints;
    private int _patrolIndex;
    private float _searchEndTime;
    private bool _telegraphing;
    private float _telegraphEndTime;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();

        if (patrolRouteParent != null)
        {
            int count = patrolRouteParent.childCount;
            _patrolPoints = new Transform[count];
            for (int i = 0; i < count; i++) _patrolPoints[i] = patrolRouteParent.GetChild(i);
        }
    }

    public void SetState(MCPEnemyState state)
    {
        if (currentState == state) return;
        currentState = state;
        _telegraphing = false;
    }

    public void OnTargetDetected(Transform detected)
    {
        target = detected;
        lastKnownTargetPosition = detected.position;
        SetState(useStalkerBehavior ? MCPEnemyState.Stalk : MCPEnemyState.Chase);
    }

    public void OnTargetLost()
    {
        if (target != null) lastKnownTargetPosition = target.position;
        _searchEndTime = Time.time + searchDuration;
        SetState(MCPEnemyState.Search);
    }

    public void OnNoiseHeard(Vector3 position)
    {
        if (currentState == MCPEnemyState.Chase || currentState == MCPEnemyState.Attack || currentState == MCPEnemyState.Stalk) return;
        lastKnownTargetPosition = position;
        _searchEndTime = Time.time + searchDuration;
        SetState(MCPEnemyState.Search);
    }

    private void Update()
    {
        switch (currentState)
        {
            case MCPEnemyState.Patrol: UpdatePatrol(); break;
            case MCPEnemyState.Chase: UpdateChase(); break;
            case MCPEnemyState.Search: UpdateSearch(); break;
            case MCPEnemyState.Attack: UpdateAttack(); break;
            case MCPEnemyState.Stalk: UpdateStalk(); break;
        }
    }

    private void UpdatePatrol()
    {
        if (_patrolPoints == null || _patrolPoints.Length == 0) return;

        _agent.speed = chaseSpeed * patrolSpeedMultiplier;
        if (!_agent.pathPending && _agent.remainingDistance < 0.5f)
        {
            _patrolIndex = (_patrolIndex + 1) % _patrolPoints.Length;
            _agent.SetDestination(_patrolPoints[_patrolIndex].position);
        }
    }

    private void UpdateChase()
    {
        if (target == null) { OnTargetLost(); return; }

        _agent.speed = chaseSpeed;
        _agent.SetDestination(target.position);

        if (Vector3.Distance(transform.position, target.position) <= attackRange)
            SetState(MCPEnemyState.Attack);
    }

    private void UpdateSearch()
    {
        _agent.SetDestination(lastKnownTargetPosition);
        if (Time.time >= _searchEndTime)
            SetState(_patrolPoints != null && _patrolPoints.Length > 0 ? MCPEnemyState.Patrol : MCPEnemyState.Idle);
    }

    private void UpdateAttack()
    {
        if (target == null) { OnTargetLost(); return; }

        _agent.SetDestination(transform.position);

        if (Vector3.Distance(transform.position, target.position) > attackRange * 1.2f)
        {
            SetState(MCPEnemyState.Chase);
            return;
        }

        if (!_telegraphing)
        {
            _telegraphing = true;
            _telegraphEndTime = Time.time + telegraphDuration;
            onAttackTelegraphed?.Invoke();
        }
        else if (Time.time >= _telegraphEndTime)
        {
            _telegraphing = false;
            _telegraphEndTime = Time.time + telegraphDuration;
            PerformAttack();
        }
    }

    private void PerformAttack()
    {
        // SendMessage, not a direct type reference: MCPMeleeAttack/MCPHitscanWeapon/MCPProjectileWeapon are
        // scaffolded by the separate weapons group and may not exist in every project that uses enemy_ai --
        // a direct GetComponent<MCPMeleeAttack>() call would make this script fail to compile whenever any one
        // of those three types is missing. SendMessage calls whichever of these methods exists on this
        // GameObject, if any, with zero compile-time coupling.
        gameObject.SendMessage("TryAttack", SendMessageOptions.DontRequireReceiver);
        gameObject.SendMessage("TryFire", SendMessageOptions.DontRequireReceiver);
    }

    private void UpdateStalk()
    {
        if (target == null) { OnTargetLost(); return; }

        float distance = Vector3.Distance(transform.position, target.position);
        if (distance < stalkerRetreatDistance)
        {
            Vector3 away = (transform.position - target.position).normalized;
            _agent.SetDestination(transform.position + away * 5f);
        }
        else if (distance > stalkerApproachDistance)
        {
            _agent.SetDestination(target.position);
        }
        else
        {
            _agent.SetDestination(transform.position);
        }
    }
}
"""


async def _scaffold_enemy_brain(bridge: UnityBridgeClient) -> bool:
    return await _scaffold_script(bridge, _ENEMY_BRAIN_PATH, _ENEMY_BRAIN_CONTENT)


@workflow(
    "create_enemy",
    "Assembles an enemy actor in one call: GameObject + NavMeshAgent + optional primitive placeholder model + "
    "MCPHealth (damage receiver) + MCPEnemyBrain (the patrol/chase/search/attack/stalk state machine), optionally "
    "with sight/hearing senses attached. Attack behavior activates automatically once a weapon component "
    "(MCPMeleeAttack/MCPHitscanWeapon/MCPProjectileWeapon, from the weapons group) is present -- add one with "
    "add_melee_attack/configure_hitscan/configure_projectile afterward.",
    {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "Name for the enemy GameObject."},
            "parentPath": {"type": "string", "description": "Hierarchy path to parent the enemy under. Omit for scene root."},
            "x": {"type": "number", "description": "World-space X position. Defaults to 0."},
            "y": {"type": "number", "description": "World-space Y position. Defaults to 0."},
            "z": {"type": "number", "description": "World-space Z position. Defaults to 0."},
            "modelPrimitive": {
                "type": "string",
                "enum": ["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"],
                "description": "If given, creates a primitive as a visual placeholder child named 'Model'. Defaults to Capsule.",
            },
            "maxHealth": {"type": "number", "description": "Starting/max health. Defaults to 100."},
            "moveSpeed": {"type": "number", "description": "NavMeshAgent speed, also used as the brain's chaseSpeed. Defaults to 3.5."},
            "addSightSensor": {"type": "boolean", "description": "Whether to attach a sight sensor. Defaults to true."},
            "addHearingSensor": {"type": "boolean", "description": "Whether to attach a hearing sensor. Defaults to true."},
        },
        "required": ["name"],
    },
    group="enemy_ai",
)
async def _create_enemy(bridge: UnityBridgeClient, args: dict) -> Any:
    name = args["name"]
    parent_path = args.get("parentPath")
    x, y, z = args.get("x", 0.0), args.get("y", 0.0), args.get("z", 0.0)
    model_primitive = args.get("modelPrimitive", "Capsule")
    max_health = args.get("maxHealth", 100)
    move_speed = args.get("moveSpeed", 3.5)
    add_sight = args.get("addSightSensor", True)
    add_hearing = args.get("addHearingSensor", True)

    enemy_args = {"name": name}
    if parent_path:
        enemy_args["parentPath"] = parent_path
    await bridge.call("create_gameobject", enemy_args)
    path = f"{parent_path}/{name}" if parent_path else name
    await bridge.call("set_transform", {"path": path, "posX": x, "posY": y, "posZ": z})

    await bridge.call("add_navmesh_agent", {"path": path, "speed": move_speed})

    await _create_damage_receiver(bridge, {"path": path, "maxHealth": max_health})

    brain_created = await _scaffold_enemy_brain(bridge)
    if brain_created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPEnemyBrain"})
    await _apply_field_batch(bridge, path, "MCPEnemyBrain", {"chaseSpeed": move_speed}, ["chaseSpeed"])

    model_path = None
    if model_primitive:
        primitive_result = await bridge.call("create_primitive", {"type": model_primitive})
        primitive_path = primitive_result["path"]
        await bridge.call("reparent_gameobject", {"path": primitive_path, "newParentPath": path})
        await bridge.call("rename_gameobject", {"path": f"{path}/{primitive_path.rsplit('/', 1)[-1]}", "newName": "Model"})
        model_path = f"{path}/Model"

    if add_sight:
        await add_sight_sensor_handler(bridge, {"path": path})
    if add_hearing:
        await add_hearing_sensor_handler(bridge, {"path": path})

    return {"path": path, "modelPath": model_path}


_SIGHT_SENSOR_PATH = "Scripts/MCP/MCPSightSensor.cs"
_SIGHT_SENSOR_CONTENT = """using UnityEngine;

public class MCPSightSensor : MonoBehaviour
{
    public Transform target;
    public float viewDistance = 15f;
    public float fieldOfViewAngle = 90f;
    public LayerMask obstructionMask = ~0;

    private MCPEnemyBrain _brain;
    private bool _hasSeenTarget;

    private void Awake()
    {
        _brain = GetComponentInParent<MCPEnemyBrain>();
        if (target == null)
        {
            var player = GameObject.FindGameObjectWithTag("Player");
            if (player != null) target = player.transform;
        }
    }

    private void Update()
    {
        if (target == null || _brain == null) return;

        Vector3 toTarget = target.position - transform.position;
        float distance = toTarget.magnitude;
        bool visible = false;

        if (distance <= viewDistance)
        {
            float angle = Vector3.Angle(transform.forward, toTarget);
            if (angle <= fieldOfViewAngle * 0.5f)
            {
                if (!Physics.Raycast(transform.position, toTarget.normalized, distance, obstructionMask))
                    visible = true;
            }
        }

        if (visible && !_hasSeenTarget)
        {
            _hasSeenTarget = true;
            _brain.OnTargetDetected(target);
        }
        else if (!visible && _hasSeenTarget)
        {
            _hasSeenTarget = false;
            _brain.OnTargetLost();
        }
    }
}
"""


@workflow(
    "add_sight_sensor",
    "Attaches/tunes MCPSightSensor: field-of-view + raycast line-of-sight vision. Calls the enemy's MCPEnemyBrain "
    "(found via GetComponentInParent, so this can sit on the enemy itself or a child 'eyes' transform) "
    "OnTargetDetected/OnTargetLost as visibility changes. Scaffolds MCPEnemyBrain as a shared dependency if missing "
    "-- if it needs to be added, use create_enemy first so the enemy has a NavMeshAgent for it to control.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to attach the sensor to."},
            "targetPath": {"type": "string", "description": "Hierarchy path of the GameObject to watch for (usually the player). Omit to auto-find a GameObject tagged 'Player' at runtime."},
            "viewDistance": {"type": "number", "description": "Maximum sight distance in meters. Defaults to 15."},
            "fieldOfViewAngle": {"type": "number", "description": "Full FOV cone angle in degrees. Defaults to 90."},
        },
        "required": ["path"],
    },
    group="enemy_ai",
)
async def add_sight_sensor_handler(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    target_path = args.get("targetPath")

    brain_created = await _scaffold_enemy_brain(bridge)
    sensor_created = await _scaffold_script(bridge, _SIGHT_SENSOR_PATH, _SIGHT_SENSOR_CONTENT)
    if brain_created or sensor_created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPSightSensor"})
    if target_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPSightSensor", "fieldName": "target", "targetGameObjectPath": target_path})
    await _apply_field_batch(bridge, path, "MCPSightSensor", args, ["viewDistance", "fieldOfViewAngle"])

    return {"path": path}


_HEARING_SENSOR_PATH = "Scripts/MCP/MCPHearingSensor.cs"
_HEARING_SENSOR_CONTENT = """using UnityEngine;

// A minimal static event bus for noise-based AI perception: anything that makes
// a noise (footsteps, gunfire, breaking glass) calls MCPNoiseEvents.Emit(...);
// anything listening for noise (MCPHearingSensor) subscribes to it. Nothing in
// this project's existing scripts (e.g. MCPFootsteps, weapon fire) emits into
// this yet -- wire that up in whichever script should make noise by calling
// MCPNoiseEvents.Emit(transform.position, radius) from it directly.
public static class MCPNoiseEvents
{
    public static System.Action<Vector3, float> OnNoiseEmitted;

    public static void Emit(Vector3 position, float radius)
    {
        OnNoiseEmitted?.Invoke(position, radius);
    }
}

public class MCPHearingSensor : MonoBehaviour
{
    public float hearingSensitivity = 1f;

    private MCPEnemyBrain _brain;

    private void Awake()
    {
        _brain = GetComponentInParent<MCPEnemyBrain>();
    }

    private void OnEnable()
    {
        MCPNoiseEvents.OnNoiseEmitted += HandleNoise;
    }

    private void OnDisable()
    {
        MCPNoiseEvents.OnNoiseEmitted -= HandleNoise;
    }

    private void HandleNoise(Vector3 position, float radius)
    {
        if (_brain == null) return;

        float distance = Vector3.Distance(transform.position, position);
        if (distance <= radius * hearingSensitivity)
            _brain.OnNoiseHeard(position);
    }
}
"""


@workflow(
    "add_hearing_sensor",
    "Attaches/tunes MCPHearingSensor: reacts to noise events broadcast via the static MCPNoiseEvents.Emit(position, "
    "radius) -- nothing emits into this yet, so wire up a noise source (footsteps, gunfire) by adding a call to "
    "MCPNoiseEvents.Emit(...) in whichever script should make noise. Scaffolds MCPEnemyBrain as a shared dependency "
    "if missing.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to attach the sensor to."},
            "hearingSensitivity": {"type": "number", "description": "Multiplier on a heard noise's radius -- higher hears fainter/farther sounds. Defaults to 1."},
        },
        "required": ["path"],
    },
    group="enemy_ai",
)
async def add_hearing_sensor_handler(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]

    brain_created = await _scaffold_enemy_brain(bridge)
    sensor_created = await _scaffold_script(bridge, _HEARING_SENSOR_PATH, _HEARING_SENSOR_CONTENT)
    if brain_created or sensor_created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPHearingSensor"})
    await _apply_field_batch(bridge, path, "MCPHearingSensor", args, ["hearingSensitivity"])

    return {"path": path}


@workflow(
    "add_patrol_route",
    "Creates a 'PatrolRoute' child holder GameObject with a waypoint child at each given position, and wires the "
    "enemy's MCPEnemyBrain.patrolRouteParent to it -- the brain auto-discovers the holder's direct children as "
    "ordered waypoints at Awake() (there's no tool-level way to wire a Transform[] array directly, only single "
    "references, so this auto-discovery pattern is used instead, same as MCPWeaponSwitcher in the weapons group). "
    "Scaffolds MCPEnemyBrain as a shared dependency if missing.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the enemy GameObject."},
            "waypointPositions": {
                "type": "array",
                "items": {"type": "string"},
                "description": "World-space waypoint positions, each as an \\\"x,y,z\\\" string, in patrol order.",
            },
            "startPatrolling": {"type": "boolean", "description": "Whether to immediately set the brain's currentState to Patrol. Defaults to true."},
        },
        "required": ["path", "waypointPositions"],
    },
    group="enemy_ai",
)
async def _add_patrol_route(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    waypoint_positions = args["waypointPositions"]
    start_patrolling = args.get("startPatrolling", True)

    await bridge.call("create_gameobject", {"name": "PatrolRoute", "parentPath": path})
    route_path = f"{path}/PatrolRoute"

    for i, pos in enumerate(waypoint_positions):
        x, y, z = (float(v) for v in pos.split(","))
        await bridge.call("create_gameobject", {"name": f"Waypoint{i}", "parentPath": route_path})
        await bridge.call("set_transform", {"path": f"{route_path}/Waypoint{i}", "posX": x, "posY": y, "posZ": z})

    brain_created = await _scaffold_enemy_brain(bridge)
    if brain_created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPEnemyBrain"})
    await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPEnemyBrain", "fieldName": "patrolRouteParent", "targetGameObjectPath": route_path})
    if start_patrolling:
        await bridge.call("set_component_properties_batch", {
            "path": path, "typeName": "MCPEnemyBrain", "fieldNames": ["currentState"], "values": ["Patrol"],
        })

    return {"path": path, "routePath": route_path, "waypointCount": len(waypoint_positions)}


@workflow(
    "configure_chase_behavior",
    "Tunes MCPEnemyBrain's chase speed and attack range (the distance at which it switches from Chase to Attack). "
    "Adds the brain (scaffolding its script if needed) if the target doesn't already have one.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the enemy GameObject."},
            "chaseSpeed": {"type": "number", "description": "NavMeshAgent speed while chasing. Defaults to 5."},
            "attackRange": {"type": "number", "description": "Distance at which Chase transitions to Attack. Defaults to 2."},
        },
        "required": ["path"],
    },
    group="enemy_ai",
)
async def _configure_chase_behavior(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    created = await _scaffold_enemy_brain(bridge)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPEnemyBrain"})
    await _apply_field_batch(bridge, path, "MCPEnemyBrain", args, ["chaseSpeed", "attackRange"])
    return {"path": path}


@workflow(
    "configure_search_behavior",
    "Tunes MCPEnemyBrain's searchDuration -- how long it investigates the last-known target position before giving "
    "up and returning to Patrol/Idle. Adds the brain (scaffolding its script if needed) if the target doesn't "
    "already have one.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the enemy GameObject."},
            "searchDuration": {"type": "number", "description": "Seconds spent searching before giving up. Defaults to 5."},
        },
        "required": ["path"],
    },
    group="enemy_ai",
)
async def _configure_search_behavior(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    created = await _scaffold_enemy_brain(bridge)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPEnemyBrain"})
    await _apply_field_batch(bridge, path, "MCPEnemyBrain", args, ["searchDuration"])
    return {"path": path}


@workflow(
    "configure_attack_behavior",
    "Tunes MCPEnemyBrain's attackRange and telegraphDuration (a wind-up delay before the hit actually lands -- wire "
    "onAttackTelegraphed to an animation/VFX/sound cue for a visible tell). The brain performs the actual attack by "
    "calling TryAttack()/TryFire() on whatever weapon component is already present (MCPMeleeAttack/"
    "MCPHitscanWeapon/MCPProjectileWeapon from the weapons group) -- add one separately with add_melee_attack/"
    "configure_hitscan/configure_projectile. Adds the brain (scaffolding its script if needed) if missing.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the enemy GameObject."},
            "attackRange": {"type": "number", "description": "Distance at which Chase transitions to Attack. Defaults to 2."},
            "telegraphDuration": {"type": "number", "description": "Wind-up seconds before each attack actually lands. Defaults to 0.5."},
        },
        "required": ["path"],
    },
    group="enemy_ai",
)
async def _configure_attack_behavior(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    created = await _scaffold_enemy_brain(bridge)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPEnemyBrain"})
    await _apply_field_batch(bridge, path, "MCPEnemyBrain", args, ["attackRange", "telegraphDuration"])
    return {"path": path}


@workflow(
    "add_stalker_ai",
    "Enables MCPEnemyBrain's stalker behavior -- the signature horror pattern of retreating out of sight when the "
    "player gets close (rather than attacking) and slowly closing the distance again once unseen/far. Adds the "
    "brain (scaffolding its script if needed) if the target doesn't already have one.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the enemy GameObject."},
            "enabled": {"type": "boolean", "description": "Whether stalker behavior is active. Defaults to true."},
            "retreatDistance": {"type": "number", "description": "Distance below which it retreats. Defaults to 15."},
            "approachDistance": {"type": "number", "description": "Distance above which it approaches. Defaults to 25."},
        },
        "required": ["path"],
    },
    group="enemy_ai",
)
async def _add_stalker_ai(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    enabled = args.get("enabled", True)

    created = await _scaffold_enemy_brain(bridge)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPEnemyBrain"})

    field_names = ["useStalkerBehavior"]
    values = [str(enabled)]
    if "retreatDistance" in args:
        field_names.append("stalkerRetreatDistance")
        values.append(str(args["retreatDistance"]))
    if "approachDistance" in args:
        field_names.append("stalkerApproachDistance")
        values.append(str(args["approachDistance"]))

    await bridge.call("set_component_properties_batch", {
        "path": path, "typeName": "MCPEnemyBrain", "fieldNames": field_names, "values": values,
    })

    return {"path": path}


_ENEMY_SPAWNER_PATH = "Scripts/MCP/MCPEnemySpawner.cs"
_ENEMY_SPAWNER_CONTENT = """using UnityEngine;

public class MCPEnemySpawner : MonoBehaviour
{
    public GameObject enemyPrefab;
    public int waveSize = 3;
    public float spawnInterval = 2f;
    public float spawnRadius = 3f;
    public bool spawnOnStart;
    public bool isSpawning;

    private int _spawnedThisWave;
    private float _nextSpawnTime;

    private void Start()
    {
        if (spawnOnStart) StartWave();
    }

    public void StartWave()
    {
        isSpawning = true;
        _spawnedThisWave = 0;
        _nextSpawnTime = Time.time;
    }

    private void Update()
    {
        if (!isSpawning || enemyPrefab == null) return;
        if (_spawnedThisWave >= waveSize) { isSpawning = false; return; }
        if (Time.time < _nextSpawnTime) return;

        Vector2 offset = Random.insideUnitCircle * spawnRadius;
        Vector3 spawnPos = transform.position + new Vector3(offset.x, 0f, offset.y);
        Object.Instantiate(enemyPrefab, spawnPos, transform.rotation);

        _spawnedThisWave++;
        _nextSpawnTime = Time.time + spawnInterval;
    }
}
"""


@workflow(
    "add_enemy_spawner",
    "Creates (or reuses) a GameObject with MCPEnemySpawner: spawns up to waveSize copies of a prefab in a radius "
    "around itself, spaced by spawnInterval. Call its public StartWave() to trigger a wave (e.g. wired to a "
    "trigger volume's UnityEvent in the Editor, or from another script) -- no trigger-condition wiring is done "
    "automatically here.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of an existing GameObject to use as the spawner. Omit to create a new one at name/x/y/z."},
            "name": {"type": "string", "description": "Name for a new spawner GameObject, if path is omitted. Defaults to 'EnemySpawner'."},
            "x": {"type": "number", "description": "World-space X position for a new spawner. Defaults to 0."},
            "y": {"type": "number", "description": "World-space Y position for a new spawner. Defaults to 0."},
            "z": {"type": "number", "description": "World-space Z position for a new spawner. Defaults to 0."},
            "enemyPrefabPath": {"type": "string", "description": "Path relative to Assets/ of the enemy prefab to spawn. Omit to leave unset."},
            "waveSize": {"type": "number", "description": "Enemies per wave. Defaults to 3."},
            "spawnInterval": {"type": "number", "description": "Seconds between individual spawns within a wave. Defaults to 2."},
            "spawnRadius": {"type": "number", "description": "Random spawn scatter radius in meters. Defaults to 3."},
            "spawnOnStart": {"type": "boolean", "description": "Whether to start a wave automatically on Play. Defaults to false."},
        },
        "required": [],
    },
    group="enemy_ai",
)
async def _add_enemy_spawner(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args.get("path")
    if not path:
        name = args.get("name", "EnemySpawner")
        await bridge.call("create_gameobject", {"name": name})
        await bridge.call("set_transform", {"path": name, "posX": args.get("x", 0.0), "posY": args.get("y", 0.0), "posZ": args.get("z", 0.0)})
        path = name

    created = await _scaffold_script(bridge, _ENEMY_SPAWNER_PATH, _ENEMY_SPAWNER_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPEnemySpawner"})

    enemy_prefab_path = args.get("enemyPrefabPath")
    if enemy_prefab_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPEnemySpawner", "fieldName": "enemyPrefab", "targetAssetPath": enemy_prefab_path})

    await _apply_field_batch(bridge, path, "MCPEnemySpawner", args, ["waveSize", "spawnInterval", "spawnRadius", "spawnOnStart"])

    return {"path": path}


# ---------------------------------------------------------------------------
# Audio -- occlusion, ambient beds, scare stingers, surface-aware footsteps,
# and layered dynamic music. add_footstep_audio_set deliberately does NOT
# extend fps_controller's MCPFootsteps script (that would create the same
# kind of cross-batch compile-time coupling enemy_ai's MCPEnemyBrain avoided
# via SendMessage) -- instead it ships a fully self-contained
# MCPSurfaceFootsteps, accepting some duplication with MCPFootsteps over a
# fragile cross-script dependency.
# ---------------------------------------------------------------------------

_AUDIO_OCCLUSION_PATH = "Scripts/MCP/MCPAudioOcclusion.cs"
_AUDIO_OCCLUSION_CONTENT = """using UnityEngine;

[RequireComponent(typeof(AudioLowPassFilter))]
public class MCPAudioOcclusion : MonoBehaviour
{
    public Transform listener;
    public LayerMask obstructionMask = ~0;
    public float occludedCutoffFrequency = 800f;
    public float clearCutoffFrequency = 22000f;
    public float transitionSpeed = 5000f;

    private AudioLowPassFilter _lowPassFilter;

    private void Awake()
    {
        _lowPassFilter = GetComponent<AudioLowPassFilter>();

        if (listener == null)
        {
            var found = Object.FindFirstObjectByType<AudioListener>();
            if (found != null) listener = found.transform;
        }
    }

    private void Update()
    {
        if (listener == null || _lowPassFilter == null) return;

        Vector3 toListener = listener.position - transform.position;
        bool occluded = Physics.Raycast(transform.position, toListener.normalized, toListener.magnitude, obstructionMask);

        float target = occluded ? occludedCutoffFrequency : clearCutoffFrequency;
        _lowPassFilter.cutoffFrequency = Mathf.MoveTowards(_lowPassFilter.cutoffFrequency, target, transitionSpeed * Time.deltaTime);
    }
}
"""


@workflow(
    "add_audio_occlusion",
    "Attaches/tunes MCPAudioOcclusion: raycasts to the listener each frame and applies a low-pass filter when "
    "something is between the source and the listener, muffling sound behind geometry. Adds an AudioLowPassFilter "
    "explicitly (rather than relying on the script's own Awake()) so it's structurally present immediately.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject with the sound source."},
            "listenerPath": {"type": "string", "description": "Hierarchy path of the AudioListener (usually the player camera). Omit to auto-find the scene's AudioListener at runtime."},
        },
        "required": ["path"],
    },
    group="audio",
)
async def _add_audio_occlusion(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    listener_path = args.get("listenerPath")

    created = await _scaffold_script(bridge, _AUDIO_OCCLUSION_PATH, _AUDIO_OCCLUSION_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "AudioLowPassFilter"})
    await bridge.call("add_component", {"path": path, "typeName": "MCPAudioOcclusion"})
    if listener_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPAudioOcclusion", "fieldName": "listener", "targetGameObjectPath": listener_path})

    return {"path": path}


@workflow(
    "add_ambient_bed",
    "Sets up a looping ambient soundscape: adds a 2D (non-spatial) AudioSource with loop enabled, and optionally a "
    "fade-in over fadeInDuration seconds (via a small scaffolded MCPAmbientFade script) instead of starting at "
    "full volume immediately.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to play the ambient bed from."},
            "clipAssetPath": {"type": "string", "description": "Path relative to Assets/ of the looping ambient AudioClip. Omit to leave unset."},
            "volume": {"type": "number", "description": "Target volume once fully faded in (or immediately, if fadeInDuration is 0). Defaults to 1."},
            "fadeInDuration": {"type": "number", "description": "Seconds to fade in from silence. Defaults to 0 (starts at full volume immediately)."},
        },
        "required": ["path"],
    },
    group="audio",
)
async def _add_ambient_bed(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    clip_asset_path = args.get("clipAssetPath")
    volume = args.get("volume", 1.0)
    fade_in_duration = args.get("fadeInDuration", 0.0)

    await bridge.call("add_audio_source", {"path": path, "spatialBlend": 0.0})

    source_props = {"path": path, "loop": True, "playOnAwake": True}
    if clip_asset_path:
        source_props["clipAssetPath"] = clip_asset_path
    source_props["volume"] = 0.0 if fade_in_duration > 0 else volume
    await bridge.call("set_audio_source_properties", source_props)

    if fade_in_duration > 0:
        created = await _scaffold_script(bridge, _AMBIENT_FADE_PATH, _AMBIENT_FADE_CONTENT)
        if created:
            await _wait_for_compile(bridge)
        await bridge.call("add_component", {"path": path, "typeName": "MCPAmbientFade"})
        await bridge.call("set_component_properties_batch", {
            "path": path, "typeName": "MCPAmbientFade",
            "fieldNames": ["targetVolume", "fadeInDuration"], "values": [str(volume), str(fade_in_duration)],
        })

    return {"path": path}


_AMBIENT_FADE_PATH = "Scripts/MCP/MCPAmbientFade.cs"
_AMBIENT_FADE_CONTENT = """using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class MCPAmbientFade : MonoBehaviour
{
    public float targetVolume = 1f;
    public float fadeInDuration = 3f;

    private AudioSource _audioSource;
    private float _elapsed;

    private void Awake()
    {
        _audioSource = GetComponent<AudioSource>();
        _audioSource.volume = 0f;
    }

    private void Update()
    {
        if (_elapsed >= fadeInDuration) return;

        _elapsed += Time.deltaTime;
        _audioSource.volume = Mathf.Lerp(0f, targetVolume, Mathf.Clamp01(_elapsed / fadeInDuration));
    }
}
"""


_SCARE_STINGER_PATH = "Scripts/MCP/MCPScareStinger.cs"
_SCARE_STINGER_CONTENT = """using UnityEngine;
using UnityEngine.Audio;

[RequireComponent(typeof(AudioSource))]
public class MCPScareStinger : MonoBehaviour
{
    public AudioClip stingerClip;
    public AudioMixer mixer;
    public string duckedParameter = "MusicVolume";
    public float duckAmount = -20f;
    public float duckDuration = 1.5f;

    private AudioSource _audioSource;
    private bool _isDucking;
    private float _originalValue;
    private float _duckEndTime;

    private void Awake()
    {
        _audioSource = GetComponent<AudioSource>();
    }

    public void Trigger()
    {
        if (stingerClip != null) _audioSource.PlayOneShot(stingerClip);

        if (mixer != null && !string.IsNullOrEmpty(duckedParameter) && !_isDucking)
        {
            if (mixer.GetFloat(duckedParameter, out _originalValue))
                mixer.SetFloat(duckedParameter, _originalValue + duckAmount);
            _isDucking = true;
        }

        _duckEndTime = Time.time + duckDuration;
    }

    private void Update()
    {
        if (_isDucking && Time.time >= _duckEndTime)
        {
            _isDucking = false;
            if (mixer != null) mixer.SetFloat(duckedParameter, _originalValue);
        }
    }
}
"""


@workflow(
    "add_scare_stinger",
    "Attaches/tunes MCPScareStinger for jumpscares: call its public Trigger() (e.g. wired to a trigger volume or "
    "sight-sensor detection event) to play a stinger clip and duck a music mixer parameter for duckDuration "
    "seconds. mixerAssetPath/duckedParameter are optional -- without them, Trigger() just plays the stinger clip.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to play the stinger from."},
            "stingerClipAssetPath": {"type": "string", "description": "Path relative to Assets/ of the stinger AudioClip. Omit to leave unset."},
            "mixerAssetPath": {"type": "string", "description": "Path relative to Assets/ of an AudioMixer with an exposed duckable parameter. Omit to skip ducking."},
            "duckedParameter": {"type": "string", "description": "Exposed mixer parameter name to duck. Defaults to 'MusicVolume'."},
            "duckAmount": {"type": "number", "description": "Decibels to subtract from the parameter's current value while ducked. Defaults to -20."},
            "duckDuration": {"type": "number", "description": "Seconds before the ducked parameter is restored. Defaults to 1.5."},
        },
        "required": ["path"],
    },
    group="audio",
)
async def _add_scare_stinger(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    stinger_clip_path = args.get("stingerClipAssetPath")
    mixer_asset_path = args.get("mixerAssetPath")

    await bridge.call("add_audio_source", {"path": path, "spatialBlend": 0.0})

    created = await _scaffold_script(bridge, _SCARE_STINGER_PATH, _SCARE_STINGER_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPScareStinger"})
    if stinger_clip_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPScareStinger", "fieldName": "stingerClip", "targetAssetPath": stinger_clip_path})
    if mixer_asset_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPScareStinger", "fieldName": "mixer", "targetAssetPath": mixer_asset_path})
    await _apply_field_batch(bridge, path, "MCPScareStinger", args, ["duckedParameter", "duckAmount", "duckDuration"])

    return {"path": path}


_SURFACE_CLIP_PATH = "Scripts/MCP/MCPSurfaceClip.cs"
_SURFACE_CLIP_CONTENT = """using UnityEngine;

public class MCPSurfaceClip : MonoBehaviour
{
    public string surfaceTag;
    public AudioClip clip;
}
"""

_SURFACE_FOOTSTEPS_PATH = "Scripts/MCP/MCPSurfaceFootsteps.cs"
_SURFACE_FOOTSTEPS_CONTENT = """using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class MCPSurfaceFootsteps : MonoBehaviour
{
    public CharacterController controller;
    public AudioClip fallbackClip;
    public float stepInterval = 0.5f;
    public float volume = 1f;
    public float minSpeedToStep = 0.5f;
    public LayerMask groundCheckMask = ~0;
    public float groundCheckDistance = 1.5f;

    private AudioSource _audioSource;
    private MCPSurfaceClip[] _surfaceClips;
    private float _stepTimer;

    private void Awake()
    {
        _audioSource = GetComponent<AudioSource>();
        if (controller == null) controller = GetComponentInParent<CharacterController>();
        _surfaceClips = GetComponentsInChildren<MCPSurfaceClip>();
    }

    private void Update()
    {
        if (controller == null) return;

        Vector3 horizontalVelocity = new Vector3(controller.velocity.x, 0f, controller.velocity.z);
        if (controller.isGrounded && horizontalVelocity.magnitude > minSpeedToStep)
        {
            _stepTimer -= Time.deltaTime;
            if (_stepTimer <= 0f)
            {
                var clip = GetClipForCurrentSurface();
                if (clip != null) _audioSource.PlayOneShot(clip, volume);
                _stepTimer = stepInterval;
            }
        }
        else
        {
            _stepTimer = 0f;
        }
    }

    private AudioClip GetClipForCurrentSurface()
    {
        if (_surfaceClips != null && _surfaceClips.Length > 0 &&
            Physics.Raycast(transform.position, Vector3.down, out RaycastHit hit, groundCheckDistance, groundCheckMask))
        {
            foreach (var entry in _surfaceClips)
                if (entry != null && entry.surfaceTag == hit.collider.tag) return entry.clip;
        }

        return fallbackClip;
    }
}
"""


@workflow(
    "add_footstep_audio_set",
    "Attaches MCPSurfaceFootsteps: a surface-tagged footstep clip bank. Each entry in surfaceClips becomes a child "
    "MCPSurfaceClip GameObject (surfaceTag + clip) -- the footstep player raycasts down each step and matches the "
    "ground collider's tag against them, falling back to fallbackClipAssetPath if nothing matches. A separate, "
    "self-contained script from fps_controller's MCPFootsteps (not an extension of it) -- MCPFootsteps.cs may not "
    "exist in every project that uses this, and referencing it directly would create the same cross-batch "
    "compile-time coupling enemy_ai's MCPEnemyBrain avoided.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the player GameObject (or a child of it -- the CharacterController is found via GetComponentInParent if not on the same object)."},
            "surfaceClips": {
                "type": "array",
                "items": {"type": "string"},
                "description": "Each entry as \\\"tag,assetPath\\\", e.g. [\\\"Concrete,Audio/StepConcrete.wav\\\", \\\"Wood,Audio/StepWood.wav\\\"].",
            },
            "fallbackClipAssetPath": {"type": "string", "description": "Path relative to Assets/ of the clip to play when no surface tag matches. Omit to leave unset."},
            "stepInterval": {"type": "number", "description": "Seconds between footstep sounds while moving. Defaults to 0.5."},
            "volume": {"type": "number", "description": "Playback volume (0-1). Defaults to 1."},
        },
        "required": ["path"],
    },
    group="audio",
)
async def _add_footstep_audio_set(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    surface_clips = args.get("surfaceClips") or []
    fallback_clip_path = args.get("fallbackClipAssetPath")

    clip_created = await _scaffold_script(bridge, _SURFACE_CLIP_PATH, _SURFACE_CLIP_CONTENT)
    footsteps_created = await _scaffold_script(bridge, _SURFACE_FOOTSTEPS_PATH, _SURFACE_FOOTSTEPS_CONTENT)
    if clip_created or footsteps_created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPSurfaceFootsteps"})

    for i, entry in enumerate(surface_clips):
        tag, clip_path = entry.split(",", 1)
        child_name = f"Surface{i}"
        await bridge.call("create_gameobject", {"name": child_name, "parentPath": path})
        child_path = f"{path}/{child_name}"
        await bridge.call("add_component", {"path": child_path, "typeName": "MCPSurfaceClip"})
        await bridge.call("set_component_field", {"path": child_path, "typeName": "MCPSurfaceClip", "fieldName": "surfaceTag", "value": tag})
        await bridge.call("wire_object_reference", {"path": child_path, "typeName": "MCPSurfaceClip", "fieldName": "clip", "targetAssetPath": clip_path})

    if fallback_clip_path:
        await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPSurfaceFootsteps", "fieldName": "fallbackClip", "targetAssetPath": fallback_clip_path})
    await _apply_field_batch(bridge, path, "MCPSurfaceFootsteps", args, ["stepInterval", "volume"])

    return {"path": path, "surfaceCount": len(surface_clips)}


_DYNAMIC_MUSIC_PATH = "Scripts/MCP/MCPDynamicMusic.cs"
_DYNAMIC_MUSIC_CONTENT = """using UnityEngine;

public class MCPDynamicMusic : MonoBehaviour
{
    [Range(0f, 1f)] public float tensionLevel;
    public float fadeSpeed = 1f;

    private AudioSource[] _layers;

    private void Awake()
    {
        _layers = GetComponentsInChildren<AudioSource>();
        foreach (var layer in _layers)
        {
            layer.loop = true;
            layer.playOnAwake = true;
            layer.volume = 0f;
        }
    }

    private void Start()
    {
        foreach (var layer in _layers) layer.Play();
    }

    public void SetTension(float level)
    {
        tensionLevel = Mathf.Clamp01(level);
    }

    private void Update()
    {
        if (_layers == null || _layers.Length == 0) return;

        for (int i = 0; i < _layers.Length; i++)
        {
            float layerPosition = _layers.Length == 1 ? 0f : (float)i / (_layers.Length - 1);
            float targetVolume = 1f - Mathf.Clamp01(Mathf.Abs(tensionLevel - layerPosition) * _layers.Length);
            _layers[i].volume = Mathf.MoveTowards(_layers[i].volume, targetVolume, fadeSpeed * Time.deltaTime);
        }
    }
}
"""


@workflow(
    "add_dynamic_music",
    "Attaches MCPDynamicMusic: each clip in layerClipPaths becomes a child looping AudioSource (auto-discovered by "
    "the component, ordered from calmest to most tense -- same auto-discovery pattern as MCPWeaponSwitcher/"
    "MCPEnemyBrain's patrol route, since there's no tool-level way to wire an AudioSource[] array). Call "
    "SetTension(0-1) to crossfade between layers based on how close the current tension is to each layer's position "
    "in the sequence.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to host the music layers under."},
            "layerClipPaths": {
                "type": "array",
                "items": {"type": "string"},
                "description": "Paths relative to Assets/ of each layer's looping AudioClip, ordered calmest to most tense.",
            },
            "fadeSpeed": {"type": "number", "description": "Volume crossfade speed between layers. Defaults to 1."},
        },
        "required": ["path", "layerClipPaths"],
    },
    group="audio",
)
async def _add_dynamic_music(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    layer_clip_paths = args["layerClipPaths"]

    for i, clip_path in enumerate(layer_clip_paths):
        layer_name = f"Layer{i}"
        await bridge.call("create_gameobject", {"name": layer_name, "parentPath": path})
        layer_path = f"{path}/{layer_name}"
        await bridge.call("add_audio_source", {"path": layer_path, "spatialBlend": 0.0})
        await bridge.call("set_audio_source_properties", {"path": layer_path, "clipAssetPath": clip_path, "loop": True, "playOnAwake": False, "volume": 0.0})

    created = await _scaffold_script(bridge, _DYNAMIC_MUSIC_PATH, _DYNAMIC_MUSIC_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPDynamicMusic"})
    await _apply_field_batch(bridge, path, "MCPDynamicMusic", args, ["fadeSpeed"])

    return {"path": path, "layerCount": len(layer_clip_paths)}


# ---------------------------------------------------------------------------
# VFX composites -- dust motes, blood splatter, breath fog. All three are
# tuned presets over create_particle_system/set_particle_module (+ add_decal
# for blood). add_blood_splatter approximates a one-shot burst via a brief,
# high emission rate on a non-looping system rather than a true simultaneous
# ParticleSystem.Emit()/SetBursts() call -- keeps set_particle_module's
# surface small instead of adding burst-specific fields for one composite.
# ---------------------------------------------------------------------------

@workflow(
    "add_dust_motes",
    "Adds a child particle system of tiny, sparse, slow-drifting dust motes for stale, still air -- thin "
    "atmosphere in abandoned rooms/attics/basements. A tuned preset over create_particle_system (World-space, "
    "sparse, long-lived) + set_particle_module (Noise module for gentle drift).",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to host the dust motes under (e.g. a room volume)."},
            "radius": {"type": "number", "description": "Radius of the dust-mote volume in world units. Defaults to 3."},
            "density": {"type": "number", "description": "Particles emitted per second. Defaults to 3 (sparse)."},
            "driftSpeed": {"type": "number", "description": "Noise strength driving the gentle drift. Defaults to 0.1."},
        },
        "required": ["path"],
    },
    group="vfx",
)
async def _add_dust_motes(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    radius = args.get("radius", 3.0)
    density = args.get("density", 3.0)
    drift_speed = args.get("driftSpeed", 0.1)

    create_result = await bridge.call("create_particle_system", {
        "name": "DustMotes", "parentPath": path,
        "duration": 20.0, "looping": True,
        "startLifetime": 15.0, "startSpeed": 0.05, "startSize": 0.03,
        "startColorR": 1.0, "startColorG": 0.95, "startColorB": 0.85, "startColorA": 0.35,
        "maxParticles": 200,
        "simulationSpace": "World",
        "shapeType": "Sphere", "shapeRadius": radius,
        "rateOverTime": density,
    })
    motes_path = create_result["path"]

    await bridge.call("set_particle_module", {"path": motes_path, "noiseStrength": drift_speed, "noiseFrequency": 0.2})

    return {"path": motes_path}


@workflow(
    "add_blood_splatter",
    "Places a one-off blood effect at a world position: an optional URP decal (if a decal material is given) plus "
    "a short, fast particle spray approximating a burst. Scene-dressing decoration for hand-placed grime/aftermath "
    "-- for a live, runtime hit-triggered version, see weapons' MCPHitReaction instead.",
    {
        "type": "object",
        "properties": {
            "x": {"type": "number", "description": "World-space X of the splatter."},
            "y": {"type": "number", "description": "World-space Y of the splatter."},
            "z": {"type": "number", "description": "World-space Z of the splatter."},
            "name": {"type": "string", "description": "Name for the new GameObject. Defaults to 'BloodSplatter'."},
            "parentPath": {"type": "string", "description": "Hierarchy path of an existing GameObject to parent under. Omit for scene root."},
            "decalMaterialAssetPath": {"type": "string", "description": "Path relative to Assets/ of a blood decal Material (URP Decal shader). Omit to skip the decal."},
            "particleBurstCount": {"type": "integer", "description": "Approximate number of particles in the spray. Defaults to 15."},
        },
        "required": ["x", "y", "z"],
    },
    group="vfx",
)
async def _add_blood_splatter(bridge: UnityBridgeClient, args: dict) -> Any:
    x, y, z = args["x"], args["y"], args["z"]
    name = args.get("name", "BloodSplatter")
    parent_path = args.get("parentPath")
    decal_material_path = args.get("decalMaterialAssetPath")
    particle_burst_count = args.get("particleBurstCount", 15)

    create_kwargs = {"name": name}
    if parent_path:
        create_kwargs["parentPath"] = parent_path
    create_result = await bridge.call("create_gameobject", create_kwargs)
    splatter_path = create_result["path"]
    await bridge.call("set_transform", {"path": splatter_path, "posX": x, "posY": y, "posZ": z})

    if decal_material_path:
        await bridge.call("add_decal", {"path": splatter_path, "materialAssetPath": decal_material_path, "sizeX": 0.5, "sizeY": 0.5, "sizeZ": 0.3})

    particle_result = await bridge.call("create_particle_system", {
        "name": "Burst", "parentPath": splatter_path,
        "duration": 0.3, "looping": False,
        "startLifetime": 0.6, "startSpeed": 3.0, "startSize": 0.08,
        "startColorR": 0.5, "startColorG": 0.0, "startColorB": 0.0, "startColorA": 1.0,
        "maxParticles": particle_burst_count,
        "shapeType": "Cone", "rateOverTime": particle_burst_count * 8,
    })
    burst_path = particle_result["path"]
    await bridge.call("play_particle_system", {"path": burst_path, "action": "Play"})

    return {"path": splatter_path, "particlePath": burst_path}


_BREATH_FOG_PATH = "Scripts/MCP/MCPBreathFog.cs"
_BREATH_FOG_CONTENT = """using UnityEngine;

[RequireComponent(typeof(ParticleSystem))]
public class MCPBreathFog : MonoBehaviour
{
    public float breathInterval = 4f;
    public int particleCount = 8;
    public bool onlyWhenCold = false;
    public bool isCold = true;

    private ParticleSystem _particleSystem;
    private float _timer;

    private void Awake()
    {
        _particleSystem = GetComponent<ParticleSystem>();
    }

    private void Update()
    {
        if (onlyWhenCold && !isCold) return;

        _timer -= Time.deltaTime;
        if (_timer <= 0f)
        {
            _particleSystem.Emit(particleCount);
            _timer = breathInterval;
        }
    }
}
"""


@workflow(
    "add_breath_fog",
    "Adds a small, periodic cold-breath puff particle system as a child of the given GameObject (typically the "
    "player camera) plus a scaffolded MCPBreathFog that pulses a burst of particles every breathInterval seconds "
    "via ParticleSystem.Emit() instead of emitting continuously.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to host the breath puff under (e.g. the player camera)."},
            "breathInterval": {"type": "number", "description": "Seconds between breath puffs. Defaults to 4."},
            "particleCount": {"type": "integer", "description": "Particles emitted per puff. Defaults to 8."},
        },
        "required": ["path"],
    },
    group="vfx",
)
async def _add_breath_fog(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]

    create_result = await bridge.call("create_particle_system", {
        "name": "BreathFog", "parentPath": path,
        "duration": 2.0, "looping": True,
        "startLifetime": 1.5, "startSpeed": 0.3, "startSize": 0.15,
        "startColorR": 1.0, "startColorG": 1.0, "startColorB": 1.0, "startColorA": 0.4,
        "maxParticles": 50,
        "simulationSpace": "Local",
        "shapeType": "Cone", "shapeRadius": 0.05,
        "rateOverTime": 0.0,
    })
    breath_path = create_result["path"]

    created = await _scaffold_script(bridge, _BREATH_FOG_PATH, _BREATH_FOG_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": breath_path, "typeName": "MCPBreathFog"})
    await _apply_field_batch(bridge, breath_path, "MCPBreathFog", args, ["breathInterval", "particleCount"])

    return {"path": breath_path}


# ---------------------------------------------------------------------------
# UI/HUD composites -- health bar, ammo counter, crosshair, interaction
# prompt, pause menu, subtitle system. add_ui_text/add_ui_image/add_ui_button/
# add_layout_group from the source catalog are treated as already covered by
# the existing ui-group atomics create_ui_element (Text/Image/Button/Panel/
# InputField in one tool) and set_layout -- the same kind of dedup enemy_ai's
# batch applied to scaffold_behavior_tree/add_bt_node/connect_bt_nodes.
# create_interaction_prompt is the one composite that needs a *real* runtime
# wire, not just decoration -- it uses the new core-group wire_unity_event
# tool (added this batch) to hook MCPInteractionRaycaster's onInteractableFound/
# onInteractableLost UnityEvents to the prompt's Show/Hide methods.
# ---------------------------------------------------------------------------

_VALUE_BAR_UI_PATH = "Scripts/MCP/MCPValueBarUI.cs"
_VALUE_BAR_UI_CONTENT = """using UnityEngine;
using UnityEngine.UI;

public class MCPValueBarUI : MonoBehaviour
{
    public Image targetImage;
    public float currentValue = 1f;
    public float maxValue = 1f;

    private void Update()
    {
        if (targetImage != null)
            targetImage.fillAmount = maxValue > 0f ? Mathf.Clamp01(currentValue / maxValue) : 0f;
    }

    public void SetValue(float current, float max)
    {
        currentValue = current;
        maxValue = max;
    }
}
"""


@workflow(
    "create_health_bar",
    "Creates a filled-Image bar widget (background panel + fill image) under an existing Canvas/UI parent, driven "
    "by a scaffolded MCPValueBarUI -- call its public SetValue(current, max) from gameplay code (e.g. MCPHealth) "
    "to update the fill; not wired to MCPHealth directly to avoid a hard dependency on the weapons group.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the parent UI element (typically a Canvas)."},
            "name": {"type": "string", "description": "Name for the bar's background GameObject. Defaults to 'HealthBar'."},
            "width": {"type": "number", "description": "Bar width in pixels. Defaults to 200."},
            "height": {"type": "number", "description": "Bar height in pixels. Defaults to 20."},
            "fillColorR": {"type": "number", "description": "Fill color red component (0-1). Defaults to 0.8 (red-ish)."},
            "fillColorG": {"type": "number", "description": "Fill color green component (0-1). Defaults to 0.1."},
            "fillColorB": {"type": "number", "description": "Fill color blue component (0-1). Defaults to 0.1."},
        },
        "required": ["path"],
    },
    group="ui",
)
async def _create_health_bar(bridge: UnityBridgeClient, args: dict) -> Any:
    return await _create_value_bar(bridge, args, default_name="HealthBar", default_color=(0.8, 0.1, 0.1))


@workflow(
    "create_ammo_counter",
    "Creates a Text HUD readout under an existing Canvas/UI parent, driven by a scaffolded MCPAmmoCounterUI -- "
    "call its public SetAmmo(current, reserve) from gameplay code to update the 'current / reserve' display.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the parent UI element (typically a Canvas)."},
            "name": {"type": "string", "description": "Name for the counter's GameObject. Defaults to 'AmmoCounter'."},
        },
        "required": ["path"],
    },
    group="ui",
)
async def _create_ammo_counter(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    name = args.get("name", "AmmoCounter")

    create_result = await bridge.call("create_ui_element", {"type": "Text", "parentPath": path, "name": name})
    counter_path = create_result["path"]
    await bridge.call("set_rect_transform", {"path": counter_path, "sizeX": 160, "sizeY": 40})

    created = await _scaffold_script(bridge, _AMMO_COUNTER_UI_PATH, _AMMO_COUNTER_UI_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": counter_path, "typeName": "MCPAmmoCounterUI"})
    await bridge.call("wire_object_reference", {"path": counter_path, "typeName": "MCPAmmoCounterUI", "fieldName": "targetText", "targetGameObjectPath": counter_path})

    return {"path": counter_path}


_AMMO_COUNTER_UI_PATH = "Scripts/MCP/MCPAmmoCounterUI.cs"
_AMMO_COUNTER_UI_CONTENT = """using UnityEngine;
using UnityEngine.UI;

public class MCPAmmoCounterUI : MonoBehaviour
{
    public Text targetText;
    public int currentAmmo;
    public int reserveAmmo;

    private void Update()
    {
        if (targetText != null) targetText.text = currentAmmo + " / " + reserveAmmo;
    }

    public void SetAmmo(int current, int reserve)
    {
        currentAmmo = current;
        reserveAmmo = reserve;
    }
}
"""


_CROSSHAIR_UI_PATH = "Scripts/MCP/MCPCrosshairUI.cs"
_CROSSHAIR_UI_CONTENT = """using UnityEngine;

public class MCPCrosshairUI : MonoBehaviour
{
    public RectTransform target;
    public float baseSize = 4f;
    public float maxSpread = 20f;
    [Range(0f, 1f)] public float spread;

    private void Awake()
    {
        if (target == null) target = GetComponent<RectTransform>();
    }

    private void Update()
    {
        if (target == null) return;
        float size = baseSize + spread * maxSpread;
        target.sizeDelta = new Vector2(size, size);
    }

    public void SetSpread(float value)
    {
        spread = Mathf.Clamp01(value);
    }
}
"""


@workflow(
    "create_crosshair",
    "Creates a single-dot reticle Image under an existing Canvas/UI parent, driven by a scaffolded MCPCrosshairUI "
    "that grows the dot's size from baseSize to baseSize+maxSpread as its public spread (0-1) increases -- call "
    "SetSpread() from weapon/movement code for spread feedback.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the parent UI element (typically a Canvas)."},
            "name": {"type": "string", "description": "Name for the crosshair's GameObject. Defaults to 'Crosshair'."},
            "baseSize": {"type": "number", "description": "Reticle size in pixels at zero spread. Defaults to 4."},
            "maxSpread": {"type": "number", "description": "Additional pixels added at full (1.0) spread. Defaults to 20."},
        },
        "required": ["path"],
    },
    group="ui",
)
async def _create_crosshair(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    name = args.get("name", "Crosshair")
    base_size = args.get("baseSize", 4.0)
    max_spread = args.get("maxSpread", 20.0)

    create_result = await bridge.call("create_ui_element", {"type": "Image", "parentPath": path, "name": name})
    crosshair_path = create_result["path"]
    await bridge.call("set_rect_transform", {"path": crosshair_path, "sizeX": base_size, "sizeY": base_size})
    await bridge.call("set_ui_color", {"path": crosshair_path, "r": 1.0, "g": 1.0, "b": 1.0, "a": 0.9})

    created = await _scaffold_script(bridge, _CROSSHAIR_UI_PATH, _CROSSHAIR_UI_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": crosshair_path, "typeName": "MCPCrosshairUI"})
    await _apply_field_batch(bridge, crosshair_path, "MCPCrosshairUI", {"baseSize": base_size, "maxSpread": max_spread}, ["baseSize", "maxSpread"])

    return {"path": crosshair_path}


_INTERACTION_PROMPT_UI_PATH = "Scripts/MCP/MCPInteractionPromptUI.cs"
_INTERACTION_PROMPT_UI_CONTENT = """using UnityEngine;
using UnityEngine.UI;

public class MCPInteractionPromptUI : MonoBehaviour
{
    public Text promptText;

    public void Show(string message)
    {
        if (promptText != null) promptText.text = message;
        gameObject.SetActive(true);
    }

    public void Hide()
    {
        gameObject.SetActive(false);
    }
}
"""


@workflow(
    "create_interaction_prompt",
    "Creates a 'Press E'-style Text prompt under an existing Canvas/UI parent (initially hidden), driven by a "
    "scaffolded MCPInteractionPromptUI. If raycasterPath is given (a GameObject with fps_controller's "
    "MCPInteractionRaycaster), really wires onInteractableFound -> Show(message) and onInteractableLost -> Hide() "
    "via wire_unity_event -- a live, working binding, not just decoration.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the parent UI element (typically a Canvas)."},
            "name": {"type": "string", "description": "Name for the prompt's GameObject. Defaults to 'InteractionPrompt'."},
            "raycasterPath": {"type": "string", "description": "Hierarchy path of a GameObject with MCPInteractionRaycaster to wire against. Omit to skip wiring."},
        },
        "required": ["path"],
    },
    group="ui",
)
async def _create_interaction_prompt(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    name = args.get("name", "InteractionPrompt")
    raycaster_path = args.get("raycasterPath")

    create_result = await bridge.call("create_ui_element", {"type": "Text", "parentPath": path, "name": name})
    prompt_path = create_result["path"]
    await bridge.call("set_rect_transform", {"path": prompt_path, "sizeX": 220, "sizeY": 40})
    await bridge.call("set_gameobject_active", {"path": prompt_path, "active": False})

    created = await _scaffold_script(bridge, _INTERACTION_PROMPT_UI_PATH, _INTERACTION_PROMPT_UI_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": prompt_path, "typeName": "MCPInteractionPromptUI"})
    await bridge.call("wire_object_reference", {"path": prompt_path, "typeName": "MCPInteractionPromptUI", "fieldName": "promptText", "targetGameObjectPath": prompt_path})

    if raycaster_path:
        await bridge.call("wire_unity_event", {
            "path": raycaster_path, "typeName": "MCPInteractionRaycaster", "eventFieldName": "onInteractableFound",
            "targetPath": prompt_path, "targetTypeName": "MCPInteractionPromptUI", "methodName": "Show",
        })
        await bridge.call("wire_unity_event", {
            "path": raycaster_path, "typeName": "MCPInteractionRaycaster", "eventFieldName": "onInteractableLost",
            "targetPath": prompt_path, "targetTypeName": "MCPInteractionPromptUI", "methodName": "Hide",
        })

    return {"path": prompt_path}


_PAUSE_MENU_UI_PATH = "Scripts/MCP/MCPPauseMenuUI.cs"
_PAUSE_MENU_UI_CONTENT = """using UnityEngine;

public class MCPPauseMenuUI : MonoBehaviour
{
    public GameObject panel;

    public void TogglePause()
    {
        bool willShow = panel != null && !panel.activeSelf;
        SetPaused(willShow);
    }

    public void Resume()
    {
        SetPaused(false);
    }

    public void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void SetPaused(bool paused)
    {
        if (panel != null) panel.SetActive(paused);
        Time.timeScale = paused ? 0f : 1f;
    }
}
"""


@workflow(
    "create_pause_menu",
    "Creates a hidden pause panel (Vertical layout with Resume/Quit buttons) under an existing Canvas, driven by a "
    "scaffolded MCPPauseMenuUI that also drives Time.timeScale (0 while paused, 1 while resumed). Button clicks are "
    "really wired via wire_unity_event, not just decorative.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the parent UI element (typically a Canvas)."},
            "name": {"type": "string", "description": "Name for the panel's GameObject. Defaults to 'PauseMenu'."},
        },
        "required": ["path"],
    },
    group="ui",
)
async def _create_pause_menu(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    name = args.get("name", "PauseMenu")

    panel_result = await bridge.call("create_ui_element", {"type": "Panel", "parentPath": path, "name": name})
    panel_path = panel_result["path"]
    await bridge.call("set_rect_transform", {"path": panel_path, "sizeX": 300, "sizeY": 200})
    await bridge.call("set_layout", {"path": panel_path, "type": "Vertical", "spacingY": 10})
    await bridge.call("set_gameobject_active", {"path": panel_path, "active": False})

    resume_result = await bridge.call("create_ui_element", {"type": "Button", "parentPath": panel_path, "name": "ResumeButton"})
    resume_path = resume_result["path"]
    quit_result = await bridge.call("create_ui_element", {"type": "Button", "parentPath": panel_path, "name": "QuitButton"})
    quit_path = quit_result["path"]

    created = await _scaffold_script(bridge, _PAUSE_MENU_UI_PATH, _PAUSE_MENU_UI_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPPauseMenuUI"})
    await bridge.call("wire_object_reference", {"path": path, "typeName": "MCPPauseMenuUI", "fieldName": "panel", "targetGameObjectPath": panel_path})
    await bridge.call("wire_unity_event", {
        "path": resume_path, "typeName": "Button", "eventFieldName": "onClick",
        "targetPath": path, "targetTypeName": "MCPPauseMenuUI", "methodName": "Resume",
    })
    await bridge.call("wire_unity_event", {
        "path": quit_path, "typeName": "Button", "eventFieldName": "onClick",
        "targetPath": path, "targetTypeName": "MCPPauseMenuUI", "methodName": "Quit",
    })

    return {"path": panel_path, "controllerPath": path}


_SUBTITLE_UI_PATH = "Scripts/MCP/MCPSubtitleUI.cs"
_SUBTITLE_UI_CONTENT = """using UnityEngine;
using UnityEngine.UI;

public class MCPSubtitleUI : MonoBehaviour
{
    public Text subtitleText;
    public float displayDuration = 3f;

    private float _timer;

    public void ShowLine(string line)
    {
        if (subtitleText != null) subtitleText.text = line;
        gameObject.SetActive(true);
        _timer = displayDuration;
    }

    private void Update()
    {
        if (_timer <= 0f) return;

        _timer -= Time.deltaTime;
        if (_timer <= 0f) gameObject.SetActive(false);
    }
}
"""


@workflow(
    "create_subtitle_system",
    "Creates a hidden subtitle Text element under an existing Canvas, driven by a scaffolded MCPSubtitleUI -- call "
    "its public ShowLine(text) from dialogue/SFX-cue code; it auto-hides after displayDuration seconds.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the parent UI element (typically a Canvas)."},
            "name": {"type": "string", "description": "Name for the subtitle GameObject. Defaults to 'Subtitles'."},
            "displayDuration": {"type": "number", "description": "Seconds a line stays visible after ShowLine() before auto-hiding. Defaults to 3."},
        },
        "required": ["path"],
    },
    group="ui",
)
async def _create_subtitle_system(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    name = args.get("name", "Subtitles")

    create_result = await bridge.call("create_ui_element", {"type": "Text", "parentPath": path, "name": name})
    subtitle_path = create_result["path"]
    await bridge.call("set_rect_transform", {"path": subtitle_path, "sizeX": 600, "sizeY": 60, "anchorMinX": 0.5, "anchorMinY": 0.0, "anchorMaxX": 0.5, "anchorMaxY": 0.0, "pivotX": 0.5, "pivotY": 0.0, "posY": 40})
    await bridge.call("set_gameobject_active", {"path": subtitle_path, "active": False})

    created = await _scaffold_script(bridge, _SUBTITLE_UI_PATH, _SUBTITLE_UI_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": subtitle_path, "typeName": "MCPSubtitleUI"})
    await bridge.call("wire_object_reference", {"path": subtitle_path, "typeName": "MCPSubtitleUI", "fieldName": "subtitleText", "targetGameObjectPath": subtitle_path})
    await _apply_field_batch(bridge, subtitle_path, "MCPSubtitleUI", args, ["displayDuration"])

    return {"path": subtitle_path}


async def _create_value_bar(bridge: UnityBridgeClient, args: dict, default_name: str, default_color: tuple) -> Any:
    path = args["path"]
    name = args.get("name", default_name)
    width = args.get("width", 200)
    height = args.get("height", 20)
    fill_r = args.get("fillColorR", default_color[0])
    fill_g = args.get("fillColorG", default_color[1])
    fill_b = args.get("fillColorB", default_color[2])

    bg_result = await bridge.call("create_ui_element", {"type": "Panel", "parentPath": path, "name": name})
    bg_path = bg_result["path"]
    await bridge.call("set_rect_transform", {"path": bg_path, "sizeX": width, "sizeY": height})
    await bridge.call("set_ui_color", {"path": bg_path, "r": 0.1, "g": 0.1, "b": 0.1, "a": 0.6})

    fill_result = await bridge.call("create_ui_element", {"type": "Image", "parentPath": bg_path, "name": "Fill"})
    fill_path = fill_result["path"]
    await bridge.call("set_rect_transform", {"path": fill_path, "anchorMinX": 0, "anchorMinY": 0, "anchorMaxX": 1, "anchorMaxY": 1, "sizeX": 0, "sizeY": 0})
    await bridge.call("set_component_properties_batch", {"path": fill_path, "typeName": "Image", "fieldNames": ["type", "fillMethod", "fillAmount"], "values": ["Filled", "Horizontal", "1"]})
    await bridge.call("set_ui_color", {"path": fill_path, "r": fill_r, "g": fill_g, "b": fill_b})

    created = await _scaffold_script(bridge, _VALUE_BAR_UI_PATH, _VALUE_BAR_UI_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": bg_path, "typeName": "MCPValueBarUI"})
    await bridge.call("wire_object_reference", {"path": bg_path, "typeName": "MCPValueBarUI", "fieldName": "targetImage", "targetGameObjectPath": fill_path})

    return {"path": bg_path, "fillPath": fill_path}


# ---------------------------------------------------------------------------
# Gameplay Systems & Data composites. create_scriptable_object (assets group)
# and create_health_system are treated as already covered -- the latter by
# weapons' create_damage_receiver (MCPHealth + optional MCPHitZone), the same
# kind of dedup enemy_ai's batch applied to scaffold_behavior_tree/etc.
# Every "attach once" scaffolded script here gets [DisallowMultipleComponent]
# so a repeated add_component call (e.g. create_key_lock_pair ensuring a door
# already created by create_door has MCPDoor) is a safe no-op rather than a
# silent duplicate -- confirmed via live spike that Undo.AddComponent returns
# null (not an exception, not a duplicate) against a type marked that way.
# ---------------------------------------------------------------------------

_SO_FIELD_TYPE_MAP = {
    "float": "float", "int": "int", "string": "string", "bool": "bool",
    "vector2": "Vector2", "vector3": "Vector3", "color": "Color",
}


@workflow(
    "define_scriptable_object_type",
    "Generates a new ScriptableObject class with the given fields. Follow up with create_scriptable_object "
    "(assets group) to instantiate an asset of it. Supported field types: float, int, string, bool, vector2, "
    "vector3, color.",
    {
        "type": "object",
        "properties": {
            "className": {"type": "string", "description": "Name for the new class, e.g. 'EnemyStats'."},
            "fields": {
                "type": "array", "items": {"type": "string"},
                "description": "Each entry as \\\"fieldName:type\\\", e.g. [\\\"maxHealth:float\\\", \\\"displayName:string\\\"].",
            },
            "createAssetMenuPath": {"type": "string", "description": "Menu path for Assets > Create > ..., e.g. 'MCP/EnemyStats'. Omit to skip the [CreateAssetMenu] attribute."},
        },
        "required": ["className"],
    },
    group="gameplay",
)
async def _define_scriptable_object_type(bridge: UnityBridgeClient, args: dict) -> Any:
    class_name = args["className"]
    fields = args.get("fields") or []
    create_asset_menu_path = args.get("createAssetMenuPath")
    path = f"Scripts/MCP/{class_name}.cs"

    field_lines = []
    for entry in fields:
        name, _, raw_type = entry.partition(":")
        cs_type = _SO_FIELD_TYPE_MAP.get(raw_type.strip().lower())
        if cs_type is None:
            raise BridgeError(f"Unsupported field type '{raw_type}' in '{entry}'. Supported: {', '.join(_SO_FIELD_TYPE_MAP)}.")
        field_lines.append(f"    public {cs_type} {name.strip()};")

    attribute = f'[CreateAssetMenu(menuName = "{create_asset_menu_path}")]\n' if create_asset_menu_path else ""
    body = ("\n".join(field_lines) + "\n") if field_lines else ""
    content = f"using UnityEngine;\n\n{attribute}public class {class_name} : ScriptableObject\n{{\n{body}}}\n"

    try:
        await bridge.call("create_script", {"path": path, "template": "PlainClass"})
    except BridgeError as e:
        if "already exists" in str(e):
            raise BridgeError(f"'{path}' already exists -- define_scriptable_object_type only creates new classes; use update_script to modify an existing one.")
        raise
    await bridge.call("update_script", {"path": path, "content": content})
    await _wait_for_compile(bridge)

    return {"path": path, "className": class_name}


_EVENT_CHANNEL_TYPE_MAP = {
    "Void": ("UnityEvent", None),
    "Float": ("UnityEvent<float>", "float"),
    "Int": ("UnityEvent<int>", "int"),
    "String": ("UnityEvent<string>", "string"),
    "Bool": ("UnityEvent<bool>", "bool"),
}


def _event_channel_script(class_name: str, arg_type: str) -> str:
    event_type, param_type = _EVENT_CHANNEL_TYPE_MAP[arg_type]
    if param_type is None:
        raise_method = "    public void Raise()\n    {\n        OnEventRaised?.Invoke();\n    }\n"
    else:
        raise_method = f"    public void Raise({param_type} value)\n    {{\n        OnEventRaised?.Invoke(value);\n    }}\n"
    return (
        "using UnityEngine;\nusing UnityEngine.Events;\n\n"
        f"public class {class_name} : ScriptableObject\n{{\n"
        f"    public {event_type} OnEventRaised;\n\n{raise_method}}}\n"
    )


@workflow(
    "create_event_channel",
    "Scaffolds an SO-based event channel class (Void/Float/Int/String/Bool payload) and creates an asset instance "
    "-- the central-event-bus pattern. Other code calls Raise()/Raise(value); subscribers are wired via "
    "wire_event_listener.",
    {
        "type": "object",
        "properties": {
            "assetPath": {"type": "string", "description": "Destination path relative to Assets/ for the channel asset, e.g. 'Data/OnPlayerDied.asset'."},
            "className": {"type": "string", "description": "Class name for the channel type. Defaults to 'MCP{argType}EventChannel', e.g. 'MCPStringEventChannel'."},
            "argType": {"type": "string", "description": "Payload type: Void, Float, Int, String, or Bool. Defaults to Void."},
        },
        "required": ["assetPath"],
    },
    group="gameplay",
)
async def _create_event_channel(bridge: UnityBridgeClient, args: dict) -> Any:
    asset_path = args["assetPath"]
    arg_type = args.get("argType", "Void")
    if arg_type not in _EVENT_CHANNEL_TYPE_MAP:
        raise BridgeError(f"Unknown argType '{arg_type}'. Valid values: {', '.join(_EVENT_CHANNEL_TYPE_MAP)}.")
    class_name = args.get("className") or f"MCP{arg_type}EventChannel"
    script_path = f"Scripts/MCP/{class_name}.cs"

    created = await _scaffold_script(bridge, script_path, _event_channel_script(class_name, arg_type))
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("create_scriptable_object", {"typeName": class_name, "assetPath": asset_path})

    return {"assetPath": asset_path, "className": class_name}


_SAVE_DATA_PATH = "Scripts/MCP/MCPSaveData.cs"
_SAVE_DATA_CONTENT = """using UnityEngine;

[DisallowMultipleComponent]
public class MCPSaveData : MonoBehaviour
{
    [TextArea] public string data = "{}";
}
"""

_SAVE_SYSTEM_PATH = "Scripts/MCP/MCPSaveSystem.cs"
_SAVE_SYSTEM_CONTENT = """using System.Collections.Generic;
using System.IO;
using UnityEngine;

[System.Serializable]
public class MCPSaveEntry
{
    public string path;
    public string data;
}

[System.Serializable]
public class MCPSaveFile
{
    public List<MCPSaveEntry> entries = new List<MCPSaveEntry>();
}

[DisallowMultipleComponent]
public class MCPSaveSystem : MonoBehaviour
{
    private static string SaveDirectory => Path.Combine(Application.persistentDataPath, "MCPSaves");

    public void SaveSlot(string slot)
    {
        Directory.CreateDirectory(SaveDirectory);
        var file = new MCPSaveFile();
        foreach (var saveData in Object.FindObjectsByType<MCPSaveData>(FindObjectsSortMode.None))
            file.entries.Add(new MCPSaveEntry { path = GetPath(saveData.transform), data = saveData.data });

        File.WriteAllText(Path.Combine(SaveDirectory, slot + ".json"), JsonUtility.ToJson(file));
    }

    public bool LoadSlot(string slot)
    {
        string path = Path.Combine(SaveDirectory, slot + ".json");
        if (!File.Exists(path)) return false;

        var file = JsonUtility.FromJson<MCPSaveFile>(File.ReadAllText(path));
        var byPath = new Dictionary<string, MCPSaveData>();
        foreach (var saveData in Object.FindObjectsByType<MCPSaveData>(FindObjectsSortMode.None))
            byPath[GetPath(saveData.transform)] = saveData;

        foreach (var entry in file.entries)
            if (byPath.TryGetValue(entry.path, out var saveData))
                saveData.data = entry.data;

        return true;
    }

    private static string GetPath(Transform t)
    {
        string path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }
}
"""


@workflow(
    "create_save_system",
    "Scaffolds a scene-wide save/load system: MCPSaveData (a small per-GameObject JSON blackboard -- attach to "
    "anything with state to persist) and MCPSaveSystem (SaveSlot/LoadSlot -- gathers every MCPSaveData in the "
    "scene into one real JSON file under Application.persistentDataPath/MCPSaves, keyed by hierarchy path, using "
    "only JsonUtility so no Newtonsoft dependency is required in the target project). Independent of the "
    "save_game_state/load_game_state atomic tools, which write arbitrary JSON directly for a quick verification "
    "loop.",
    {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "Name for the new MCPSaveSystem GameObject. Defaults to 'SaveSystem'."},
        },
        "required": [],
    },
    group="gameplay",
)
async def _create_save_system(bridge: UnityBridgeClient, args: dict) -> Any:
    name = args.get("name", "SaveSystem")

    save_data_created = await _scaffold_script(bridge, _SAVE_DATA_PATH, _SAVE_DATA_CONTENT)
    save_system_created = await _scaffold_script(bridge, _SAVE_SYSTEM_PATH, _SAVE_SYSTEM_CONTENT)
    if save_data_created or save_system_created:
        await _wait_for_compile(bridge)

    create_result = await bridge.call("create_gameobject", {"name": name})
    path = create_result["path"]
    await bridge.call("add_component", {"path": path, "typeName": "MCPSaveSystem"})

    return {"path": path}


_GAME_MANAGER_PATH = "Scripts/MCP/MCPGameManager.cs"
_GAME_MANAGER_CONTENT = """using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
public class MCPGameManager : MonoBehaviour
{
    public string currentState = "MainMenu";
    public UnityEvent<string> onStateChanged;

    public void SetState(string newState)
    {
        currentState = newState;
        onStateChanged?.Invoke(newState);
    }
}
"""


@workflow(
    "create_game_manager",
    "Scaffolds MCPGameManager: a minimal central game-state holder (MainMenu/Playing/Paused/GameOver, or any "
    "custom string) with a SetState(state) method firing onStateChanged for UI/audio/etc to hook into.",
    {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "Name for the GameObject. Defaults to 'GameManager'."},
            "initialState": {"type": "string", "description": "Starting state. Defaults to 'MainMenu'."},
        },
        "required": [],
    },
    group="gameplay",
)
async def _create_game_manager(bridge: UnityBridgeClient, args: dict) -> Any:
    name = args.get("name", "GameManager")
    initial_state = args.get("initialState", "MainMenu")

    created = await _scaffold_script(bridge, _GAME_MANAGER_PATH, _GAME_MANAGER_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    create_result = await bridge.call("create_gameobject", {"name": name})
    path = create_result["path"]
    await bridge.call("add_component", {"path": path, "typeName": "MCPGameManager"})
    await _apply_field_batch(bridge, path, "MCPGameManager", {"currentState": initial_state}, ["currentState"])

    return {"path": path}


_INVENTORY_PATH = "Scripts/MCP/MCPInventory.cs"
_INVENTORY_CONTENT = """using UnityEngine;

[DisallowMultipleComponent]
public class MCPInventory : MonoBehaviour
{
    [TextArea] public string data = "{}";
}
"""


@workflow(
    "create_inventory_system",
    "Attaches MCPInventory: a JSON blackboard (item id -> count) on the given GameObject, the same "
    "get_component_field/set_component_field-driven pattern as set_blackboard_key -- no dedicated add/remove-item "
    "tool is added here, staying in scope for this batch.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to attach the inventory to (usually the player)."},
        },
        "required": ["path"],
    },
    group="gameplay",
)
async def _create_inventory_system(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    created = await _scaffold_script(bridge, _INVENTORY_PATH, _INVENTORY_CONTENT)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPInventory"})
    return {"path": path}


_INTERACTABLE_PATH = "Scripts/MCP/MCPInteractable.cs"
_INTERACTABLE_CONTENT = """using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
public class MCPInteractable : MonoBehaviour, IInteractable
{
    public string promptMessage = "Interact";
    public UnityEvent onInteract;

    public string GetInteractionPrompt() => promptMessage;

    public void Interact()
    {
        onInteract?.Invoke();
    }
}
"""


@workflow(
    "create_interactable",
    "Attaches a generic MCPInteractable (implements the shared IInteractable interface fps_controller's "
    "add_interaction_raycaster/MCPInteractionRaycaster already looks for) to an existing GameObject -- for "
    "levers/pickups/anything with a simple 'show a prompt, fire an event on interact' shape. For doors/keys, see "
    "create_door/create_key_lock_pair instead.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the target GameObject."},
            "promptMessage": {"type": "string", "description": "Text shown by the interaction prompt. Defaults to 'Interact'."},
        },
        "required": ["path"],
    },
    group="gameplay",
)
async def _create_interactable(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    interface_created = await _scaffold_script(bridge, _INTERACTABLE_INTERFACE_PATH, _INTERACTABLE_INTERFACE_CONTENT)
    interactable_created = await _scaffold_script(bridge, _INTERACTABLE_PATH, _INTERACTABLE_CONTENT)
    if interface_created or interactable_created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": path, "typeName": "MCPInteractable"})
    await _apply_field_batch(bridge, path, "MCPInteractable", args, ["promptMessage"])

    return {"path": path}


_DOOR_PATH = "Scripts/MCP/MCPDoor.cs"
_DOOR_CONTENT = """using UnityEngine;

[DisallowMultipleComponent]
public class MCPDoor : MonoBehaviour, IInteractable
{
    public bool isOpen;
    public bool isLocked;
    public string requiredKeyId = "";
    public float openAngle = 90f;
    public float openSpeed = 2f;

    private Quaternion _closedRotation;
    private Quaternion _openRotation;

    private void Awake()
    {
        _closedRotation = transform.localRotation;
        _openRotation = _closedRotation * Quaternion.Euler(0f, openAngle, 0f);
    }

    private void Update()
    {
        Quaternion target = isOpen ? _openRotation : _closedRotation;
        transform.localRotation = Quaternion.RotateTowards(transform.localRotation, target, openSpeed * 90f * Time.deltaTime);
    }

    public string GetInteractionPrompt()
    {
        if (isLocked) return "Locked";
        return isOpen ? "Close" : "Open";
    }

    public void Interact()
    {
        if (isLocked) return;
        isOpen = !isOpen;
    }

    public void Unlock(string keyId)
    {
        if (requiredKeyId == keyId) isLocked = false;
    }
}
"""


@workflow(
    "create_door",
    "Creates a new GameObject (a placeholder Cube -- swap the mesh manually) with a scaffolded MCPDoor: rotates "
    "open/closed on Interact() (blocked while isLocked), implements the shared IInteractable interface. Pair "
    "with create_key_lock_pair for a matching key.",
    {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "Name for the new GameObject. Defaults to 'Door'."},
            "parentPath": {"type": "string", "description": "Hierarchy path of an existing GameObject to parent under. Omit for scene root."},
            "x": {"type": "number", "description": "World-space X position."},
            "y": {"type": "number", "description": "World-space Y position."},
            "z": {"type": "number", "description": "World-space Z position."},
            "isLocked": {"type": "boolean", "description": "Whether the door starts locked. Defaults to false."},
            "requiredKeyId": {"type": "string", "description": "Key id that unlocks this door, matched against MCPKeyItem.keyId. Defaults to empty."},
            "openAngle": {"type": "number", "description": "Degrees the door swings open. Defaults to 90."},
        },
        "required": [],
    },
    group="gameplay",
)
async def _create_door(bridge: UnityBridgeClient, args: dict) -> Any:
    name = args.get("name", "Door")
    x, y, z = args.get("x", 0.0), args.get("y", 0.0), args.get("z", 0.0)
    parent_path = args.get("parentPath")
    is_locked = args.get("isLocked", False)
    required_key_id = args.get("requiredKeyId", "")
    open_angle = args.get("openAngle", 90.0)

    interface_created = await _scaffold_script(bridge, _INTERACTABLE_INTERFACE_PATH, _INTERACTABLE_INTERFACE_CONTENT)
    door_created = await _scaffold_script(bridge, _DOOR_PATH, _DOOR_CONTENT)
    if interface_created or door_created:
        await _wait_for_compile(bridge)

    primitive_result = await bridge.call("create_primitive", {"type": "Cube", "x": x, "y": y, "z": z})
    primitive_path = primitive_result["path"]
    if parent_path:
        await bridge.call("reparent_gameobject", {"path": primitive_path, "newParentPath": parent_path})
        primitive_path = f"{parent_path}/{primitive_path.rsplit('/', 1)[-1]}"
    await bridge.call("rename_gameobject", {"path": primitive_path, "newName": name})
    door_path = primitive_path.rsplit("/", 1)[0] + "/" + name if "/" in primitive_path else name

    await bridge.call("add_component", {"path": door_path, "typeName": "MCPDoor"})
    await _apply_field_batch(bridge, door_path, "MCPDoor", {
        "isLocked": is_locked, "requiredKeyId": required_key_id, "openAngle": open_angle,
    }, ["isLocked", "requiredKeyId", "openAngle"])

    return {"path": door_path}


_KEY_ITEM_PATH = "Scripts/MCP/MCPKeyItem.cs"
_KEY_ITEM_CONTENT = """using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
public class MCPKeyItem : MonoBehaviour, IInteractable
{
    public string keyId = "";
    public string promptMessage = "Pick up Key";
    public UnityEvent<string> onPickup;

    public string GetInteractionPrompt() => promptMessage;

    public void Interact()
    {
        onPickup?.Invoke(keyId);
        Destroy(gameObject);
    }
}
"""


@workflow(
    "create_key_lock_pair",
    "Creates a pickup key (MCPKeyItem, implements IInteractable -- fires onPickup(keyId) then destroys itself) "
    "and wires it directly to an existing (or newly-scaffolded) door's MCPDoor.Unlock(keyId) via wire_unity_event's "
    "dynamic mode, so the door receives whichever real keyId the key raises rather than a hardcoded one.",
    {
        "type": "object",
        "properties": {
            "keyId": {"type": "string", "description": "Identifier matched between the key and the door's requiredKeyId."},
            "doorPath": {"type": "string", "description": "Hierarchy path of the door GameObject (MCPDoor is attached if missing; requiredKeyId is set to keyId and isLocked to true)."},
            "keyName": {"type": "string", "description": "Name for the new key GameObject. Defaults to 'Key'."},
            "x": {"type": "number", "description": "World-space X position for the key."},
            "y": {"type": "number", "description": "World-space Y position for the key."},
            "z": {"type": "number", "description": "World-space Z position for the key."},
        },
        "required": ["keyId", "doorPath"],
    },
    group="gameplay",
)
async def _create_key_lock_pair(bridge: UnityBridgeClient, args: dict) -> Any:
    key_id = args["keyId"]
    door_path = args["doorPath"]
    key_name = args.get("keyName", "Key")
    x, y, z = args.get("x", 0.0), args.get("y", 0.0), args.get("z", 0.0)

    interface_created = await _scaffold_script(bridge, _INTERACTABLE_INTERFACE_PATH, _INTERACTABLE_INTERFACE_CONTENT)
    door_created = await _scaffold_script(bridge, _DOOR_PATH, _DOOR_CONTENT)
    key_created = await _scaffold_script(bridge, _KEY_ITEM_PATH, _KEY_ITEM_CONTENT)
    if interface_created or door_created or key_created:
        await _wait_for_compile(bridge)

    primitive_result = await bridge.call("create_primitive", {"type": "Sphere", "x": x, "y": y, "z": z})
    key_path = primitive_result["path"]
    await bridge.call("rename_gameobject", {"path": key_path, "newName": key_name})
    key_path = key_name

    await bridge.call("add_component", {"path": key_path, "typeName": "MCPKeyItem"})
    await _apply_field_batch(bridge, key_path, "MCPKeyItem", {"keyId": key_id}, ["keyId"])

    await bridge.call("add_component", {"path": door_path, "typeName": "MCPDoor"})
    await _apply_field_batch(bridge, door_path, "MCPDoor", {"requiredKeyId": key_id, "isLocked": True}, ["requiredKeyId", "isLocked"])

    await bridge.call("wire_unity_event", {
        "path": key_path, "typeName": "MCPKeyItem", "eventFieldName": "onPickup",
        "targetPath": door_path, "targetTypeName": "MCPDoor", "methodName": "Unlock",
    })

    return {"keyPath": key_path, "doorPath": door_path}


_CHECKPOINT_PATH = "Scripts/MCP/MCPCheckpoint.cs"
_CHECKPOINT_CONTENT = """using UnityEngine;

[DisallowMultipleComponent]
public class MCPCheckpoint : MonoBehaviour
{
    public Vector3 respawnPosition;
    public bool isActive;

    private void Awake()
    {
        respawnPosition = transform.position;
    }

    public void Activate()
    {
        isActive = true;
    }

    public void Respawn(GameObject target)
    {
        if (target == null) return;
        var controller = target.GetComponent<CharacterController>();
        if (controller != null) controller.enabled = false;
        target.transform.position = respawnPosition;
        if (controller != null) controller.enabled = true;
    }
}
"""


@workflow(
    "create_checkpoint",
    "Creates a trigger volume (via add_trigger_volume) plus a scaffolded MCPCheckpoint that records its own "
    "position as a respawn point and exposes Respawn(target) to move a player back here (temporarily disabling "
    "the target's CharacterController around the teleport -- the standard way to avoid it fighting a large "
    "position jump). onTriggerEnter is wired to Activate() via wire_unity_event's static-listener fallback, since "
    "MCPCheckpoint doesn't need to know which Collider entered.",
    {
        "type": "object",
        "properties": {
            "x": {"type": "number", "description": "World-space X position."},
            "y": {"type": "number", "description": "World-space Y position."},
            "z": {"type": "number", "description": "World-space Z position."},
            "name": {"type": "string", "description": "Name for the new GameObject. Defaults to 'Checkpoint'."},
            "radius": {"type": "number", "description": "Trigger sphere radius. Defaults to 2."},
        },
        "required": ["x", "y", "z"],
    },
    group="gameplay",
)
async def _create_checkpoint(bridge: UnityBridgeClient, args: dict) -> Any:
    x, y, z = args["x"], args["y"], args["z"]
    name = args.get("name", "Checkpoint")
    radius = args.get("radius", 2.0)

    create_result = await bridge.call("create_gameobject", {"name": name})
    path = create_result["path"]
    await bridge.call("set_transform", {"path": path, "posX": x, "posY": y, "posZ": z})

    await bridge.call("add_trigger_volume", {"path": path, "shape": "Sphere", "radius": radius})

    created = await _scaffold_script(bridge, _CHECKPOINT_PATH, _CHECKPOINT_CONTENT)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPCheckpoint"})

    await bridge.call("wire_unity_event", {
        "path": path, "typeName": "MCPTriggerRelay", "eventFieldName": "onTriggerEnter",
        "targetPath": path, "targetTypeName": "MCPCheckpoint", "methodName": "Activate",
    })

    return {"path": path}


_OBJECTIVE_SYSTEM_PATH = "Scripts/MCP/MCPObjectiveSystem.cs"
_OBJECTIVE_SYSTEM_CONTENT = """using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
public class MCPObjectiveSystem : MonoBehaviour
{
    [TextArea] public string data = "{}";
    public UnityEvent<string> onObjectiveCompleted;

    public void CompleteObjective(string objectiveId)
    {
        onObjectiveCompleted?.Invoke(objectiveId);
    }
}
"""

_OBJECTIVE_LIST_UI_PATH = "Scripts/MCP/MCPObjectiveListUI.cs"
_OBJECTIVE_LIST_UI_CONTENT = """using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class MCPObjectiveListUI : MonoBehaviour
{
    public Text listText;

    public void AppendLine(string line)
    {
        if (listText == null) return;
        listText.text += (string.IsNullOrEmpty(listText.text) ? "" : "\\n") + line;
    }
}
"""


@workflow(
    "create_objective_system",
    "Attaches MCPObjectiveSystem: a JSON blackboard (objective id -> state, same get_component_field/"
    "set_component_field pattern as set_blackboard_key) plus a CompleteObjective(id) method firing "
    "onObjectiveCompleted for other systems/UI to react to. If uiCanvasPath is given, also creates a Text list "
    "under it (scaffolded MCPObjectiveListUI) really wired via wire_unity_event to append each completed "
    "objective id as it's raised.",
    {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Hierarchy path of the GameObject to host the objective system (e.g. the game manager)."},
            "uiCanvasPath": {"type": "string", "description": "Hierarchy path of a Canvas to create a completed-objectives Text list under. Omit to skip the UI hook."},
        },
        "required": ["path"],
    },
    group="gameplay",
)
async def _create_objective_system(bridge: UnityBridgeClient, args: dict) -> Any:
    path = args["path"]
    ui_canvas_path = args.get("uiCanvasPath")

    created = await _scaffold_script(bridge, _OBJECTIVE_SYSTEM_PATH, _OBJECTIVE_SYSTEM_CONTENT)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPObjectiveSystem"})

    ui_path = None
    if ui_canvas_path:
        create_result = await bridge.call("create_ui_element", {"type": "Text", "parentPath": ui_canvas_path, "name": "ObjectiveList"})
        ui_path = create_result["path"]
        await bridge.call("set_rect_transform", {"path": ui_path, "sizeX": 300, "sizeY": 200})

        ui_created = await _scaffold_script(bridge, _OBJECTIVE_LIST_UI_PATH, _OBJECTIVE_LIST_UI_CONTENT)
        if ui_created:
            await _wait_for_compile(bridge)
        await bridge.call("add_component", {"path": ui_path, "typeName": "MCPObjectiveListUI"})
        await bridge.call("wire_object_reference", {"path": ui_path, "typeName": "MCPObjectiveListUI", "fieldName": "listText", "targetGameObjectPath": ui_path})
        await bridge.call("wire_unity_event", {
            "path": path, "typeName": "MCPObjectiveSystem", "eventFieldName": "onObjectiveCompleted",
            "targetPath": ui_path, "targetTypeName": "MCPObjectiveListUI", "methodName": "AppendLine",
        })

    return {"path": path, "uiPath": ui_path}


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

def _tool_description_char_total(unity_tools: list[dict], groups_filter: set[str]) -> int:
    total = sum(len(t.get("description", "")) for t in unity_tools if (t.get("group") or "core") in groups_filter)
    total += sum(len(wf.description) for wf in all_workflows() if wf.group in groups_filter)
    return total


_SOFT_ACTIVE_TOKEN_BUDGET = 8000


def _param_search_text(schema: dict) -> str:
    props = (schema or {}).get("properties") or {}
    return " ".join(
        f"{name} {prop.get('description', '')}" for name, prop in props.items() if isinstance(prop, dict)
    )


def _search_summary(description: str, max_chars: int = 140) -> str:
    if not description:
        return ""
    period = description.find(". ")
    if 0 < period <= max_chars:
        return description[: period + 1]
    if len(description) <= max_chars:
        return description
    return description[:max_chars].rsplit(" ", 1)[0] + "..."


async def _build_tool_search_index(bridge: UnityBridgeClient) -> tuple[tool_search.ToolSearchIndex, list[dict]]:
    try:
        unity_tools = await bridge.list_tools()
    except BridgeError:
        unity_tools = []

    docs = []
    for t in unity_tools:
        group = t.get("group") or "core"
        if tool_groups.is_disabled(group):
            continue
        docs.append(tool_search.ToolDoc(
            name=t["name"], group=group, description=t.get("description", ""),
            param_text=_param_search_text(t.get("schema")),
        ))

    for wf in all_workflows():
        if tool_groups.is_disabled(wf.group):
            continue
        docs.append(tool_search.ToolDoc(
            name=wf.name, group=wf.group, description=wf.description,
            param_text=_param_search_text(wf.schema), is_composite=True,
        ))

    for group_name, group_desc in tool_groups.GROUP_CATALOG.items():
        if tool_groups.is_disabled(group_name):
            continue
        docs.append(tool_search.ToolDoc(group=group_name, description=group_desc))

    return tool_search.ToolSearchIndex(docs), unity_tools


_CATALOG_TRAILING_STOPWORDS = {"and", "or", "of", "plus", "the", "a", "an", "with", "for"}


def _truncate_group_description(description: str, max_chars: int = 70) -> str:
    for sep in (". ", ": "):
        idx = description.find(sep)
        if 0 < idx <= max_chars:
            return description[:idx]
    snippet = description if len(description) <= max_chars else description[:max_chars].rsplit(" ", 1)[0]
    snippet = snippet.rstrip(".,;: ")
    words = snippet.split(" ")
    while words and words[-1].lower().strip(",") in _CATALOG_TRAILING_STOPWORDS:
        words.pop()
    return " ".join(words)


def _compact_group_catalog() -> str:
    """One-line-per-group summary for inlining directly into manage_tools' own always-
    visible description (see docs/tool-scaling-strategy.md section 4.1) -- built fresh
    from GROUP_CATALOG so it can never drift out of sync with the real group list."""
    return "; ".join(f"{name}: {_truncate_group_description(desc)}" for name, desc in tool_groups.GROUP_CATALOG.items())


@workflow(
    "manage_tools",
    "Controls which tool groups are visible in this session's tool list. Most tools are hidden by default to keep "
    "the visible tool list focused (fewer tokens, better routing). Actions: search (find the right tool/group by "
    "keyword BEFORE activating anything, e.g. query='flickering light scare' -- prefer this over list_groups when "
    "you don't already know exactly which group you need), list_groups (every group, its description, whether "
    "it's active, and which tools it contains), activate (make one or more groups' tools visible -- pass 'group' "
    "for one or 'groups' for several at once), deactivate ('core' cannot be deactivated -- it holds the essential "
    "scene/component/query tools plus batch_execute and this tool itself), reset (back to only 'core'). "
    f"Groups at a glance: {_compact_group_catalog()}",
    {
        "type": "object",
        "properties": {
            "action": {"type": "string", "enum": ["search", "list_groups", "activate", "deactivate", "reset"]},
            "query": {
                "type": "string",
                "description": "Required for search. Free-text keywords, e.g. 'terrain sculpting' or 'scare sequence'.",
            },
            "limit": {"type": "number", "description": "Max results for search. Defaults to 8."},
            "group": {
                "type": "string",
                "description": f"For activate/deactivate on a single group. One of: {', '.join(tool_groups.GROUP_CATALOG)}",
            },
            "groups": {
                "type": "array", "items": {"type": "string"},
                "description": "For activate/deactivate on multiple groups at once. Takes precedence over 'group' if both are given.",
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

    if action == "search":
        query = args.get("query")
        if not query:
            raise BridgeError("'query' is required for search.")
        limit = int(args.get("limit", 8))

        index, _ = await _build_tool_search_index(bridge)
        hits = index.search(query, limit=limit)
        active = tool_groups.get_active_groups()

        results = []
        for doc in hits:
            if doc.name is None:
                results.append({"group": doc.group, "groupMatch": True, "active": doc.group in active, "summary": doc.description})
            else:
                results.append({"tool": doc.name, "group": doc.group, "active": doc.group in active, "summary": _search_summary(doc.description)})

        return {
            "results": results,
            "hint": "Call manage_tools(action=\"activate\", groups=[...]) for the group(s) above before calling a listed tool directly.",
        }

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

        # Disabled groups (set via the Unity Editor's Tool Groups window) are excluded
        # entirely, not just marked inactive -- a client must not be able to discover
        # they exist at all from this listing, matching the same "unknown" treatment
        # activate/deactivate give them below.
        return {
            "groups": [
                {
                    "group": name,
                    "description": description,
                    "active": tool_groups.is_active(name),
                    "tools": sorted(tool_names_by_group.get(name, [])),
                }
                for name, description in tool_groups.GROUP_CATALOG.items()
                if not tool_groups.is_disabled(name)
            ]
        }

    if action in ("activate", "deactivate"):
        group_names = args.get("groups")
        if group_names is None:
            single = args.get("group")
            if not single:
                raise BridgeError("'group' or 'groups' is required for activate/deactivate.")
            group_names = [single]
        if not isinstance(group_names, list) or not group_names:
            raise BridgeError("'groups' must be a non-empty array of group names.")

        # A disabled group is treated exactly like an unrecognized name -- deliberately
        # indistinguishable from a genuinely unknown group, so this error can't be used
        # to infer that a disabled group exists.
        valid_groups = [g for g in tool_groups.GROUP_CATALOG if not tool_groups.is_disabled(g)]
        unknown = [g for g in group_names if g not in valid_groups]
        if unknown:
            raise BridgeError(f"Unknown group(s): {', '.join(unknown)}. Valid groups: {', '.join(valid_groups)}")

        if action == "deactivate" and "core" in group_names:
            raise BridgeError("'core' cannot be deactivated -- it contains the tools every session needs.")

        warning = None
        if action == "activate":
            # Soft budget guard (see docs/tool-scaling-strategy.md section 5): warn, but
            # still proceed, if activating would push the active set's estimated
            # description-token cost past a soft threshold. Real, current tool
            # descriptions are fetched fresh, not an estimate baked in ahead of time.
            try:
                unity_tools = await bridge.list_tools()
            except BridgeError:
                unity_tools = []
            prospective_active = tool_groups.get_active_groups() | set(group_names)
            estimated_tokens = _tool_description_char_total(unity_tools, prospective_active) // 4
            if estimated_tokens > _SOFT_ACTIVE_TOKEN_BUDGET:
                warning = (
                    f"Activating {', '.join(group_names)} brings the estimated active tool-description cost to "
                    f"~{estimated_tokens} tokens (soft budget: {_SOFT_ACTIVE_TOKEN_BUDGET}). Consider deactivating "
                    "groups you're done with."
                )
            for g in group_names:
                tool_groups.activate(g)
        else:
            for g in group_names:
                tool_groups.deactivate(g)

        result = {"active": sorted(tool_groups.get_active_groups())}
        if warning:
            result["warning"] = warning
        return result

    raise BridgeError(f"Unknown action '{action}'.")


# ---------------------------------------------------------------------------
# Batch 17 composites -- terrain (scatter_props), levelgen (grid/spawn/room/
# streaming/navmesh-validation), timeline (create_scare_sequence), and input
# (generate_input_reader, add_rebinding_ui). All new atomic tools for terrain/
# levelgen/timeline/input live in C# (TerrainTools.cs, LevelGenTools.cs,
# TimelineTools.cs, InputTools.cs); these ten are pure Python compositions
# over those plus existing core/gameplay/audio/cameras tools -- no new
# reflection or Unity-side code needed.
# ---------------------------------------------------------------------------


@workflow(
    "scatter_props",
    "Procedurally scatters instances of a prop prefab within a circular area on the ground, via repeated "
    "instantiate_prefab + snap_to_ground (each instance is placed at a random point in the circle, then dropped "
    "straight down onto whatever collider is below it) with an optional random Y rotation per instance. Pass a "
    "seed for a reproducible layout. Points that don't land on a collider within snap_to_ground's search distance "
    "are left at the scatter area's Y height rather than aborting the whole batch.",
    {
        "type": "object",
        "properties": {
            "prefabPath": {"type": "string", "description": "Path relative to Assets/ of the prop prefab to scatter."},
            "count": {"type": "number", "description": "Number of instances to place. Defaults to 10."},
            "centerX": {"type": "number", "description": "Scatter circle center X. Defaults to 0."},
            "centerY": {"type": "number", "description": "Fallback Y if an instance doesn't land on a collider. Defaults to 0."},
            "centerZ": {"type": "number", "description": "Scatter circle center Z. Defaults to 0."},
            "radius": {"type": "number", "description": "Scatter circle radius in meters. Defaults to 10."},
            "randomizeYRotation": {"type": "boolean", "description": "Whether to give each instance a random Y rotation. Defaults to true."},
            "parentPath": {"type": "string", "description": "Hierarchy path to parent the instances under. Omit to place at scene root."},
            "seed": {"type": "number", "description": "Random seed for a reproducible layout. Omit for a different layout each call."},
        },
        "required": ["prefabPath"],
    },
    group="terrain",
)
async def _scatter_props(bridge: UnityBridgeClient, args: dict) -> Any:
    prefab_path = args["prefabPath"]
    count = int(args.get("count", 10))
    center_x, center_y, center_z = args.get("centerX", 0.0), args.get("centerY", 0.0), args.get("centerZ", 0.0)
    radius = args.get("radius", 10.0)
    randomize_rotation = args.get("randomizeYRotation", True)
    parent_path = args.get("parentPath")
    rng = random.Random(args.get("seed"))

    paths = []
    for _ in range(count):
        angle = rng.uniform(0.0, 2.0 * math.pi)
        r = radius * math.sqrt(rng.uniform(0.0, 1.0))
        x = center_x + r * math.cos(angle)
        z = center_z + r * math.sin(angle)

        result = await bridge.call("instantiate_prefab", {
            "assetPath": prefab_path, "parentPath": parent_path, "posX": x, "posY": center_y, "posZ": z,
        })
        path = result["path"]

        try:
            await bridge.call("snap_to_ground", {"path": path})
        except BridgeError:
            pass  # nothing below this sample point -- leave it at centerY instead of failing the whole scatter

        if randomize_rotation:
            await bridge.call("set_transform", {"path": path, "rotY": rng.uniform(0.0, 360.0)})

        paths.append(path)

    return {"paths": paths, "count": len(paths)}


@workflow(
    "generate_grid_layout",
    "Lays out a rows x cols grid of level blockout cells under a single parent GameObject, spaced by cellSize. "
    "With roomPrefabPath, each cell instantiates that prefab (via instantiate_prefab); without it, each cell is an "
    "empty anchor GameObject at that grid position, ready for carve_room or manual placement. Returns every cell's "
    "path and grid/world coordinates.",
    {
        "type": "object",
        "properties": {
            "rows": {"type": "number", "description": "Number of rows. Defaults to 3."},
            "cols": {"type": "number", "description": "Number of columns. Defaults to 3."},
            "cellSize": {"type": "number", "description": "Spacing between cells in meters. Defaults to 10."},
            "originX": {"type": "number", "description": "World-space X of cell (0,0). Defaults to 0."},
            "originZ": {"type": "number", "description": "World-space Z of cell (0,0). Defaults to 0."},
            "y": {"type": "number", "description": "World-space Y for every cell. Defaults to 0."},
            "roomPrefabPath": {"type": "string", "description": "Path relative to Assets/ of a room prefab to instantiate per cell. Omit for empty anchor GameObjects."},
            "parentName": {"type": "string", "description": "Name for the grid's parent GameObject. Defaults to 'LevelGrid'."},
        },
        "required": [],
    },
    group="levelgen",
)
async def _generate_grid_layout(bridge: UnityBridgeClient, args: dict) -> Any:
    rows = int(args.get("rows", 3))
    cols = int(args.get("cols", 3))
    cell_size = args.get("cellSize", 10.0)
    origin_x, origin_z = args.get("originX", 0.0), args.get("originZ", 0.0)
    y = args.get("y", 0.0)
    room_prefab_path = args.get("roomPrefabPath")
    parent_name = args.get("parentName", "LevelGrid")

    await bridge.call("create_gameobject", {"name": parent_name})

    cells = []
    for r in range(rows):
        for c in range(cols):
            x = origin_x + c * cell_size
            z = origin_z + r * cell_size
            if room_prefab_path:
                result = await bridge.call("instantiate_prefab", {
                    "assetPath": room_prefab_path, "parentPath": parent_name, "posX": x, "posY": y, "posZ": z,
                })
                path = result["path"]
            else:
                result = await bridge.call("create_gameobject", {"name": f"Cell_{r}_{c}", "parentPath": parent_name})
                path = result["path"]
                await bridge.call("set_transform", {"path": path, "posX": x, "posY": y, "posZ": z})
            cells.append({"row": r, "col": c, "path": path, "x": x, "y": y, "z": z})

    return {"path": parent_name, "cells": cells}


@workflow(
    "place_spawn_points",
    "Procedurally distributes a set of player/enemy/item spawn markers (empty GameObjects) within a circular area, "
    "with rejection sampling to keep them at least minDistance apart. Pass a seed for a reproducible layout. Fails "
    "clearly (placing nothing) if count points can't be found within radius/minDistance after a bounded number of "
    "attempts, rather than silently returning fewer than asked.",
    {
        "type": "object",
        "properties": {
            "spawnType": {"type": "string", "description": "Label used in generated names, e.g. 'Enemy', 'Item', 'Player'. Defaults to 'Player'."},
            "count": {"type": "number", "description": "Number of spawn points to place. Defaults to 1."},
            "centerX": {"type": "number", "description": "Scatter circle center X. Defaults to 0."},
            "centerY": {"type": "number", "description": "Y position for every spawn point. Defaults to 0."},
            "centerZ": {"type": "number", "description": "Scatter circle center Z. Defaults to 0."},
            "radius": {"type": "number", "description": "Scatter circle radius in meters. Defaults to 10."},
            "minDistance": {"type": "number", "description": "Minimum distance between spawn points. Defaults to 0 (no minimum)."},
            "seed": {"type": "number", "description": "Random seed for a reproducible layout. Omit for a different layout each call."},
            "parentName": {"type": "string", "description": "Name for the parent GameObject spawn points are grouped under. Defaults to 'SpawnPoints'."},
        },
        "required": [],
    },
    group="levelgen",
)
async def _place_spawn_points(bridge: UnityBridgeClient, args: dict) -> Any:
    spawn_type = args.get("spawnType", "Player")
    count = int(args.get("count", 1))
    center_x, center_y, center_z = args.get("centerX", 0.0), args.get("centerY", 0.0), args.get("centerZ", 0.0)
    radius = args.get("radius", 10.0)
    min_distance = args.get("minDistance", 0.0)
    parent_name = args.get("parentName", "SpawnPoints")
    rng = random.Random(args.get("seed"))

    await bridge.call("create_gameobject", {"name": parent_name})

    placed: list[tuple[float, float]] = []
    paths = []
    max_attempts = max(count * 50, 50)
    attempts = 0
    while len(placed) < count and attempts < max_attempts:
        attempts += 1
        angle = rng.uniform(0.0, 2.0 * math.pi)
        r = radius * math.sqrt(rng.uniform(0.0, 1.0))
        x = center_x + r * math.cos(angle)
        z = center_z + r * math.sin(angle)
        if min_distance > 0 and any(math.hypot(x - px, z - pz) < min_distance for px, pz in placed):
            continue

        placed.append((x, z))
        name = f"{spawn_type}Spawn" if count == 1 else f"{spawn_type}Spawn_{len(placed)}"
        result = await bridge.call("create_gameobject", {"name": name, "parentPath": parent_name})
        path = result["path"]
        await bridge.call("set_transform", {"path": path, "posX": x, "posY": center_y, "posZ": z})
        paths.append(path)

    if len(placed) < count:
        raise BridgeError(
            f"place_spawn_points: only placed {len(placed)}/{count} points with minDistance={min_distance} within "
            f"radius={radius} after {max_attempts} attempts -- try a larger radius or smaller minDistance/count."
        )

    return {"parentPath": parent_name, "paths": paths}


def _find_hierarchy_node(nodes: list[dict], path: str) -> Optional[dict]:
    for node in nodes:
        if node["path"] == path:
            return node
        found = _find_hierarchy_node(node.get("children", []), path)
        if found is not None:
            return found
    return None


def _collect_by_name_prefix(node: dict, prefix: str) -> list[str]:
    matches = []
    for child in node.get("children", []):
        if child["name"].startswith(prefix):
            matches.append(child["path"])
        matches.extend(_collect_by_name_prefix(child, prefix))
    return matches


@workflow(
    "carve_room",
    "Instantiates a room module prefab at a position/rotation and reports its connector points -- child objects "
    "whose name starts with connectorNamePrefix (e.g. 'Connector_North'), the convention room prefabs use to mark "
    "doorways. Feed two rooms' connector paths into connect_rooms to snap them together.",
    {
        "type": "object",
        "properties": {
            "roomPrefabPath": {"type": "string", "description": "Path relative to Assets/ of the room module prefab."},
            "x": {"type": "number", "description": "World-space X position. Defaults to 0."},
            "y": {"type": "number", "description": "World-space Y position. Defaults to 0."},
            "z": {"type": "number", "description": "World-space Z position. Defaults to 0."},
            "rotY": {"type": "number", "description": "Y rotation in degrees. Defaults to 0."},
            "name": {"type": "string", "description": "Rename the instantiated room to this. Omit to keep the prefab's name."},
            "parentPath": {"type": "string", "description": "Hierarchy path to parent the room under. Omit for scene root."},
            "connectorNamePrefix": {"type": "string", "description": "Name prefix identifying connector child objects. Defaults to 'Connector'."},
        },
        "required": ["roomPrefabPath"],
    },
    group="levelgen",
)
async def _carve_room(bridge: UnityBridgeClient, args: dict) -> Any:
    room_prefab_path = args["roomPrefabPath"]
    x, y, z = args.get("x", 0.0), args.get("y", 0.0), args.get("z", 0.0)
    rot_y = args.get("rotY", 0.0)
    name = args.get("name")
    parent_path = args.get("parentPath")
    connector_prefix = args.get("connectorNamePrefix", "Connector")

    result = await bridge.call("instantiate_prefab", {
        "assetPath": room_prefab_path, "parentPath": parent_path, "posX": x, "posY": y, "posZ": z,
    })
    path = result["path"]

    if rot_y:
        await bridge.call("set_transform", {"path": path, "rotY": rot_y})

    if name:
        await bridge.call("rename_gameobject", {"path": path, "newName": name})
        parts = path.split("/")
        parts[-1] = name
        path = "/".join(parts)

    hierarchy = await bridge.call("get_hierarchy", {})
    node = _find_hierarchy_node(hierarchy["roots"], path)
    connectors = _collect_by_name_prefix(node, connector_prefix) if node else []

    return {"path": path, "connectors": connectors}


@workflow(
    "connect_rooms",
    "Joins two room modules by rigidly moving/rotating the moving room so its connector lines up with the fixed "
    "room's connector, facing the opposite direction (the standard dungeon-graph connector-snap technique) -- doors "
    "end up flush against each other. Y-axis rotation only (a flat, grid-based layout assumption, matching "
    "generate_grid_layout). Assumes movingRoomPath is a scene-root object (no parent transform), so its local and "
    "world space coincide.",
    {
        "type": "object",
        "properties": {
            "fixedConnectorPath": {"type": "string", "description": "Hierarchy path of the connector on the room that stays put."},
            "movingRoomPath": {"type": "string", "description": "Hierarchy path of the room module to move into place. Must be a scene-root object."},
            "movingConnectorPath": {"type": "string", "description": "Hierarchy path of the connector on the moving room."},
        },
        "required": ["fixedConnectorPath", "movingRoomPath", "movingConnectorPath"],
    },
    group="levelgen",
)
async def _connect_rooms(bridge: UnityBridgeClient, args: dict) -> Any:
    fixed_connector_path = args["fixedConnectorPath"]
    moving_room_path = args["movingRoomPath"]
    moving_connector_path = args["movingConnectorPath"]

    fixed_t = await bridge.call("get_transform", {"path": fixed_connector_path})
    moving_conn_t = await bridge.call("get_transform", {"path": moving_connector_path})
    moving_room_t = await bridge.call("get_transform", {"path": moving_room_path})

    fixed_pos = fixed_t["worldPosition"]
    fixed_rot_y = fixed_t["worldEulerAngles"]["y"]
    moving_conn_pos = moving_conn_t["worldPosition"]
    moving_conn_rot_y = moving_conn_t["worldEulerAngles"]["y"]
    moving_room_pos = moving_room_t["worldPosition"]
    moving_room_rot_y = moving_room_t["worldEulerAngles"]["y"]

    target_rot_y = (fixed_rot_y + 180.0) % 360.0
    delta_rot_y = target_rot_y - moving_conn_rot_y

    offset_x = moving_conn_pos["x"] - moving_room_pos["x"]
    offset_z = moving_conn_pos["z"] - moving_room_pos["z"]
    rad = math.radians(delta_rot_y)
    cos_r, sin_r = math.cos(rad), math.sin(rad)
    rotated_offset_x = offset_x * cos_r - offset_z * sin_r
    rotated_offset_z = offset_x * sin_r + offset_z * cos_r

    new_x = fixed_pos["x"] - rotated_offset_x
    new_z = fixed_pos["z"] - rotated_offset_z
    new_rot_y = (moving_room_rot_y + delta_rot_y) % 360.0

    await bridge.call("set_transform", {
        "path": moving_room_path, "posX": new_x, "posY": moving_room_pos["y"], "posZ": new_z, "rotY": new_rot_y,
    })

    return {"movingRoomPath": moving_room_path, "position": {"x": new_x, "y": moving_room_pos["y"], "z": new_z}, "rotationY": new_rot_y}


_SCENE_STREAMER_PATH = "Scripts/MCP/MCPSceneStreamer.cs"
_SCENE_STREAMER_CONTENT = """using UnityEngine;
using UnityEngine.SceneManagement;

public class MCPSceneStreamer : MonoBehaviour
{
    public string sceneName;
    private bool _loaded;

    public void LoadStream()
    {
        if (_loaded || string.IsNullOrEmpty(sceneName)) return;
        SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
        _loaded = true;
    }

    public void UnloadStream()
    {
        if (!_loaded || string.IsNullOrEmpty(sceneName)) return;
        SceneManager.UnloadSceneAsync(sceneName);
        _loaded = false;
    }
}
"""


@workflow(
    "set_scene_streaming",
    "Creates a trigger zone (via add_trigger_volume) that additively loads a scene by name when the player enters "
    "it and unloads it on exit, via a scaffolded MCPSceneStreamer wired to MCPTriggerRelay's onTriggerEnter/"
    "onTriggerExit. The streamed scene must already be registered in Build Settings (see add_scene_to_build) for "
    "SceneManager.LoadSceneAsync to find it by name at runtime.",
    {
        "type": "object",
        "properties": {
            "sceneName": {"type": "string", "description": "Name of the scene to stream in/out (as it appears in Build Settings, not an Assets/ path)."},
            "x": {"type": "number", "description": "World-space X of the trigger zone. Defaults to 0."},
            "y": {"type": "number", "description": "World-space Y of the trigger zone. Defaults to 0."},
            "z": {"type": "number", "description": "World-space Z of the trigger zone. Defaults to 0."},
            "radius": {"type": "number", "description": "Trigger sphere radius. Defaults to 5."},
            "name": {"type": "string", "description": "Name for the trigger zone GameObject. Defaults to '{sceneName}StreamZone'."},
        },
        "required": ["sceneName"],
    },
    group="levelgen",
)
async def _set_scene_streaming(bridge: UnityBridgeClient, args: dict) -> Any:
    scene_name = args["sceneName"]
    x, y, z = args.get("x", 0.0), args.get("y", 0.0), args.get("z", 0.0)
    radius = args.get("radius", 5.0)
    name = args.get("name", f"{scene_name}StreamZone")

    create_result = await bridge.call("create_gameobject", {"name": name})
    path = create_result["path"]
    await bridge.call("set_transform", {"path": path, "posX": x, "posY": y, "posZ": z})
    await bridge.call("add_trigger_volume", {"path": path, "shape": "Sphere", "radius": radius})

    created = await _scaffold_script(bridge, _SCENE_STREAMER_PATH, _SCENE_STREAMER_CONTENT)
    if created:
        await _wait_for_compile(bridge)
    await bridge.call("add_component", {"path": path, "typeName": "MCPSceneStreamer"})
    await _apply_field_batch(bridge, path, "MCPSceneStreamer", {"sceneName": scene_name}, ["sceneName"])

    await bridge.call("wire_unity_event", {
        "path": path, "typeName": "MCPTriggerRelay", "eventFieldName": "onTriggerEnter",
        "targetPath": path, "targetTypeName": "MCPSceneStreamer", "methodName": "LoadStream",
    })
    await bridge.call("wire_unity_event", {
        "path": path, "typeName": "MCPTriggerRelay", "eventFieldName": "onTriggerExit",
        "targetPath": path, "targetTypeName": "MCPSceneStreamer", "methodName": "UnloadStream",
    })

    return {"path": path, "sceneName": scene_name}


@workflow(
    "validate_level_navmesh",
    "Confirms a set of key points (spawn points, objectives, doors, ...) are reachable on the baked NavMesh: adds a "
    "temporary NavMeshAgent at originX/Y/Z and calls set_agent_destination toward each point, using its own "
    "documented reachability-test behavior (pathStatus == 'Complete'), then removes the temporary agent. Run "
    "bake_navmesh first -- an unbaked scene will just report every point unreachable.",
    {
        "type": "object",
        "properties": {
            "originX": {"type": "number", "description": "World-space X to test reachability from (e.g. the player start). Defaults to 0."},
            "originY": {"type": "number", "description": "World-space Y of the origin. Defaults to 0."},
            "originZ": {"type": "number", "description": "World-space Z of the origin. Defaults to 0."},
            "points": {
                "type": "array",
                "items": {"type": "object", "properties": {
                    "x": {"type": "number"}, "y": {"type": "number"}, "z": {"type": "number"}, "label": {"type": "string"},
                }},
                "description": "Key points to check, e.g. [{\"x\":10,\"y\":0,\"z\":5,\"label\":\"EnemySpawn_1\"}].",
            },
        },
        "required": ["points"],
    },
    group="levelgen",
)
async def _validate_level_navmesh(bridge: UnityBridgeClient, args: dict) -> Any:
    origin_x, origin_y, origin_z = args.get("originX", 0.0), args.get("originY", 0.0), args.get("originZ", 0.0)
    points = args["points"]

    agent_name = "MCPNavValidationAgent"
    create_result = await bridge.call("create_gameobject", {"name": agent_name})
    agent_path = create_result["path"]
    await bridge.call("set_transform", {"path": agent_path, "posX": origin_x, "posY": origin_y, "posZ": origin_z})
    await bridge.call("add_navmesh_agent", {"path": agent_path})

    results = []
    try:
        for point in points:
            dest = await bridge.call("set_agent_destination", {
                "path": agent_path, "x": point["x"], "y": point["y"], "z": point["z"],
            })
            results.append({
                "point": point, "reachable": dest["pathStatus"] == "Complete", "pathStatus": dest["pathStatus"],
            })
    finally:
        await bridge.call("delete_gameobject", {"path": agent_path, "confirm": True})

    return {"results": results, "allReachable": all(r["reachable"] for r in results)}


def _signal_asset_path(timeline_asset_path: str, suffix: str) -> str:
    directory, _, filename = timeline_asset_path.rpartition("/")
    stem = filename[:-len(".playable")] if filename.lower().endswith(".playable") else filename
    return f"{directory}/{stem}_{suffix}.asset" if directory else f"{stem}_{suffix}.asset"


@workflow(
    "create_scare_sequence",
    "Choreographs a scripted scare on a new Timeline: an Activation track flickers a light for a duration, an "
    "optional Animation track plays a clip on an Animator, and Signal tracks fire an audio stinger "
    "(MCPScareStinger.Trigger) and/or a camera shake (Cinemachine.CinemachineImpulseSource.GenerateImpulse) at "
    "precise moments. Each beat is optional and wired only if its *Path argument is given -- this composite only "
    "choreographs the timing, it assumes the target GameObjects already have the relevant components set up (e.g. "
    "from add_flicker_light/add_scare_stinger/add_camera_shake).",
    {
        "type": "object",
        "properties": {
            "timelineAssetPath": {"type": "string", "description": "Destination path relative to Assets/ for the new TimelineAsset, e.g. 'Timelines/JumpScare.playable'."},
            "directorPath": {"type": "string", "description": "Hierarchy path for the PlayableDirector GameObject -- created if it doesn't exist. Defaults to 'ScareDirector'."},
            "lightPath": {"type": "string", "description": "GameObject to activate for a flicker duration (e.g. one with MCPFlickerLight). Omit to skip this beat."},
            "lightStart": {"type": "number", "description": "Seconds into the sequence the light activates. Defaults to 0."},
            "lightDuration": {"type": "number", "description": "How long the light stays active. Defaults to 2."},
            "animatorPath": {"type": "string", "description": "GameObject with an Animator to drive. Omit to skip this beat."},
            "animationClipPath": {"type": "string", "description": "Path relative to Assets/ of the AnimationClip to play on animatorPath. Required if animatorPath is given."},
            "animStart": {"type": "number", "description": "Seconds into the sequence the animation clip starts. Defaults to 0."},
            "animDuration": {"type": "number", "description": "Animation clip duration on the track. Defaults to 2."},
            "audioSourcePath": {"type": "string", "description": "GameObject with MCPScareStinger to trigger. Omit to skip this beat."},
            "audioTriggerTime": {"type": "number", "description": "Seconds into the sequence the stinger fires. Defaults to 0.1."},
            "cameraShakePath": {"type": "string", "description": "GameObject with a Cinemachine.CinemachineImpulseSource to fire. Omit to skip this beat."},
            "shakeTriggerTime": {"type": "number", "description": "Seconds into the sequence the camera shake fires. Defaults to 0.15."},
        },
        "required": ["timelineAssetPath"],
    },
    group="timeline",
)
async def _create_scare_sequence(bridge: UnityBridgeClient, args: dict) -> Any:
    timeline_asset_path = args["timelineAssetPath"]
    director_path = args.get("directorPath", "ScareDirector")

    create_result = await bridge.call("create_timeline", {"assetPath": timeline_asset_path, "directorPath": director_path})
    resolved_director_path = create_result["directorPath"]

    tracks_added = []

    light_path = args.get("lightPath")
    if light_path:
        await bridge.call("add_timeline_track", {"timelineAssetPath": timeline_asset_path, "trackType": "Activation", "trackName": "Light"})
        await bridge.call("bind_timeline_track", {
            "directorPath": resolved_director_path, "timelineAssetPath": timeline_asset_path, "trackName": "Light", "targetPath": light_path,
        })
        await bridge.call("add_timeline_clip", {
            "timelineAssetPath": timeline_asset_path, "trackName": "Light",
            "start": args.get("lightStart", 0.0), "duration": args.get("lightDuration", 2.0),
        })
        tracks_added.append("Light")

    animator_path = args.get("animatorPath")
    if animator_path:
        animation_clip_path = args.get("animationClipPath")
        if not animation_clip_path:
            raise BridgeError("create_scare_sequence: animationClipPath is required when animatorPath is given.")
        await bridge.call("add_timeline_track", {"timelineAssetPath": timeline_asset_path, "trackType": "Animation", "trackName": "Anim"})
        await bridge.call("bind_timeline_track", {
            "directorPath": resolved_director_path, "timelineAssetPath": timeline_asset_path, "trackName": "Anim",
            "targetPath": animator_path, "targetTypeName": "Animator",
        })
        await bridge.call("add_timeline_clip", {
            "timelineAssetPath": timeline_asset_path, "trackName": "Anim",
            "start": args.get("animStart", 0.0), "duration": args.get("animDuration", 2.0), "clipAssetPath": animation_clip_path,
        })
        tracks_added.append("Anim")

    audio_source_path = args.get("audioSourcePath")
    if audio_source_path:
        await bridge.call("add_timeline_track", {"timelineAssetPath": timeline_asset_path, "trackType": "Signal", "trackName": "AudioCue"})
        await bridge.call("add_timeline_signal", {
            "timelineAssetPath": timeline_asset_path, "trackName": "AudioCue", "time": args.get("audioTriggerTime", 0.1),
            "signalAssetPath": _signal_asset_path(timeline_asset_path, "AudioSignal"),
            "receiverPath": audio_source_path, "targetTypeName": "MCPScareStinger", "methodName": "Trigger",
        })
        tracks_added.append("AudioCue")

    camera_shake_path = args.get("cameraShakePath")
    if camera_shake_path:
        await bridge.call("add_timeline_track", {"timelineAssetPath": timeline_asset_path, "trackType": "Signal", "trackName": "CameraShakeCue"})
        await bridge.call("add_timeline_signal", {
            "timelineAssetPath": timeline_asset_path, "trackName": "CameraShakeCue", "time": args.get("shakeTriggerTime", 0.15),
            "signalAssetPath": _signal_asset_path(timeline_asset_path, "CameraShakeSignal"),
            "receiverPath": camera_shake_path, "targetTypeName": "Cinemachine.CinemachineImpulseSource", "methodName": "GenerateImpulse",
        })
        tracks_added.append("CameraShakeCue")

    return {"timelineAssetPath": create_result["assetPath"], "directorPath": resolved_director_path, "tracksAdded": tracks_added}


_INPUT_READER_EVENT_TYPE_MAP = {"button": None, "float": "float", "vector2": "Vector2", "vector3": "Vector3"}


def _input_reader_script(class_name: str, map_name: str, actions: list[tuple[str, str]], create_asset_menu_path: Optional[str]) -> str:
    event_lines, handler_lines, subscribe_lines, unsubscribe_lines = [], [], [], []
    for action_name, action_type in actions:
        payload = _INPUT_READER_EVENT_TYPE_MAP[action_type]
        if payload is None:
            event_lines.append(f"    public event Action On{action_name};")
            handler_lines.append(f"    private void On{action_name}Performed(InputAction.CallbackContext ctx) => On{action_name}?.Invoke();")
            subscribe_lines.append(f'        _map.FindAction("{action_name}").performed += On{action_name}Performed;')
            unsubscribe_lines.append(f'        _map.FindAction("{action_name}").performed -= On{action_name}Performed;')
        else:
            event_lines.append(f"    public event Action<{payload}> On{action_name};")
            handler_lines.append(
                f"    private void On{action_name}Performed(InputAction.CallbackContext ctx) => On{action_name}?.Invoke(ctx.ReadValue<{payload}>());\n"
                f"    private void On{action_name}Canceled(InputAction.CallbackContext ctx) => On{action_name}?.Invoke(default);"
            )
            subscribe_lines.append(f'        _map.FindAction("{action_name}").performed += On{action_name}Performed;')
            subscribe_lines.append(f'        _map.FindAction("{action_name}").canceled += On{action_name}Canceled;')
            unsubscribe_lines.append(f'        _map.FindAction("{action_name}").performed -= On{action_name}Performed;')
            unsubscribe_lines.append(f'        _map.FindAction("{action_name}").canceled -= On{action_name}Canceled;')

    attribute = f'[CreateAssetMenu(menuName = "{create_asset_menu_path}")]\n' if create_asset_menu_path else ""
    events = "\n".join(event_lines)
    handlers = "\n\n".join(handler_lines)
    subs = "\n".join(subscribe_lines)
    unsubs = "\n".join(unsubscribe_lines)

    return f"""using System;
using UnityEngine;
using UnityEngine.InputSystem;

{attribute}public class {class_name} : ScriptableObject
{{
    public InputActionAsset inputActions;
    public string actionMapName = "{map_name}";

{events}

    private InputActionMap _map;

    private void OnEnable()
    {{
        if (inputActions == null) return;
        _map = inputActions.FindActionMap(actionMapName, true);
        _map.Enable();
{subs}
    }}

    private void OnDisable()
    {{
        if (_map == null) return;
{unsubs}
        _map.Disable();
    }}

{handlers}
}}
"""


@workflow(
    "generate_input_reader",
    "Scaffolds a ScriptableObject-based InputReader: a decoupled input layer exposing one C# event per action "
    "(event Action for button actions, event Action<T> for value actions), so gameplay scripts subscribe to typed "
    "events instead of touching the Input System directly. Hooks the given action map at runtime via "
    "InputActionMap.FindAction against whatever actions add_input_action has already created -- no Unity C# "
    "code-generation step required. Supported action types: button, float, vector2, vector3.",
    {
        "type": "object",
        "properties": {
            "className": {"type": "string", "description": "Name for the new InputReader class, e.g. 'PlayerInputReader'."},
            "mapName": {"type": "string", "description": "Name of the action map this reader listens to."},
            "actions": {
                "type": "array", "items": {"type": "string"},
                "description": "Each entry as \\\"ActionName:type\\\", e.g. [\\\"Jump:button\\\", \\\"Move:vector2\\\"].",
            },
            "createAssetMenuPath": {"type": "string", "description": "Menu path for Assets > Create > .... Omit to skip [CreateAssetMenu]."},
            "assetPath": {"type": "string", "description": "If given, also instantiates a ScriptableObject asset of this class at this path (relative to Assets/) via create_scriptable_object."},
        },
        "required": ["className", "mapName", "actions"],
    },
    group="input",
)
async def _generate_input_reader(bridge: UnityBridgeClient, args: dict) -> Any:
    class_name = args["className"]
    map_name = args["mapName"]
    create_asset_menu_path = args.get("createAssetMenuPath")
    asset_path = args.get("assetPath")

    parsed = []
    for entry in args["actions"]:
        action_name, _, raw_type = entry.partition(":")
        action_type = raw_type.strip().lower()
        if action_type not in _INPUT_READER_EVENT_TYPE_MAP:
            raise BridgeError(f"Unsupported action type '{raw_type}' in '{entry}'. Supported: {', '.join(_INPUT_READER_EVENT_TYPE_MAP)}.")
        parsed.append((action_name.strip(), action_type))

    path = f"Scripts/MCP/{class_name}.cs"
    content = _input_reader_script(class_name, map_name, parsed, create_asset_menu_path)

    try:
        await bridge.call("create_script", {"path": path, "template": "PlainClass"})
    except BridgeError as e:
        if "already exists" in str(e):
            raise BridgeError(f"'{path}' already exists -- generate_input_reader only creates new classes; use update_script to modify an existing one.")
        raise
    await bridge.call("update_script", {"path": path, "content": content})
    await _wait_for_compile(bridge)

    result = {"path": path, "className": class_name}
    if asset_path:
        await bridge.call("create_scriptable_object", {"typeName": class_name, "assetPath": asset_path})
        result["assetPath"] = asset_path
    return result


@workflow(
    "analyze_performance",
    "Flags common horror-FPS performance red flags: too many realtime lights, too many colliders (from "
    "get_scene_stats, scene group), and -- only when a real frame has actually rendered -- too many draw calls or "
    "SetPass calls (from get_render_stats, profiling group), a common overdraw/material-switching signal. Combines "
    "existing tools rather than adding new scene-querying capability of its own.",
    {
        "type": "object",
        "properties": {
            "maxRealtimeLights": {"type": "number", "description": "Warn above this many Light components in the scene. Defaults to 8."},
            "maxColliders": {"type": "number", "description": "Warn above this many Collider components in the scene. Defaults to 200."},
            "maxDrawCalls": {"type": "number", "description": "Warn above this many draw calls (only checked once a real frame has rendered). Defaults to 500."},
            "maxSetPassCalls": {"type": "number", "description": "Warn above this many SetPass calls (only checked once a real frame has rendered). Defaults to 100."},
        },
        "required": [],
    },
    group="profiling",
)
async def _analyze_performance(bridge: UnityBridgeClient, args: dict) -> Any:
    scene_stats = await bridge.call("get_scene_stats", {})
    render_stats = await bridge.call("get_render_stats", {})
    memory = await bridge.call("get_memory_snapshot", {"topCount": 5})

    max_lights = args.get("maxRealtimeLights", 8)
    max_colliders = args.get("maxColliders", 200)
    max_draw_calls = args.get("maxDrawCalls", 500)
    max_set_pass_calls = args.get("maxSetPassCalls", 100)

    issues = []
    if scene_stats["lightCount"] > max_lights:
        issues.append(
            f"{scene_stats['lightCount']} Light components in the scene (over {max_lights}) -- realtime lighting "
            "cost scales with light count; consider baking some or reducing real-time lights."
        )
    if scene_stats["colliderCount"] > max_colliders:
        issues.append(
            f"{scene_stats['colliderCount']} Collider components in the scene (over {max_colliders}) -- consider "
            "simplifying collision geometry or disabling colliders on decorative props."
        )

    notes = []
    render_available = render_stats["drawCalls"] > 0 or render_stats["batches"] > 0
    if render_available:
        if render_stats["drawCalls"] > max_draw_calls:
            issues.append(f"{render_stats['drawCalls']} draw calls (over {max_draw_calls}) -- consider batching or fewer unique materials.")
        if render_stats["setPassCalls"] > max_set_pass_calls:
            issues.append(f"{render_stats['setPassCalls']} SetPass calls (over {max_set_pass_calls}) -- a common overdraw/material-switching signal.")
    else:
        notes.append("Render stats are all zero -- no frame has rendered yet; run this during/after Play Mode or a Game view repaint for draw-call/batching analysis.")

    return {
        "sceneStats": scene_stats, "renderStats": render_stats, "memory": memory,
        "issues": issues, "notes": notes, "healthy": len(issues) == 0,
    }


_REBIND_BUTTON_PATH = "Scripts/MCP/MCPRebindButton.cs"
_REBIND_BUTTON_CONTENT = """using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class MCPRebindButton : MonoBehaviour
{
    public InputActionAsset actions;
    public string actionMapName;
    public string actionName;
    public int bindingIndex;
    public Text promptLabel;

    private InputActionRebindingExtensions.RebindingOperation _rebindOperation;

    private void Awake()
    {
        string key = PrefsKey();
        if (actions != null && !string.IsNullOrEmpty(key) && PlayerPrefs.HasKey(key))
            actions.LoadBindingOverridesFromJson(PlayerPrefs.GetString(key));
    }

    public void StartRebind()
    {
        var map = actions.FindActionMap(actionMapName, true);
        var action = map.FindAction(actionName, true);
        action.Disable();

        if (promptLabel != null) promptLabel.text = "Press a key...";

        _rebindOperation = action.PerformInteractiveRebinding(bindingIndex)
            .WithControlsExcluding("Mouse")
            .OnMatchWaitForAnother(0.1f)
            .OnComplete(op =>
            {
                op.Dispose();
                action.Enable();
                SaveOverrides();
                UpdateLabel(action);
            })
            .OnCancel(op =>
            {
                op.Dispose();
                action.Enable();
                UpdateLabel(action);
            })
            .Start();
    }

    private void UpdateLabel(InputAction action)
    {
        if (promptLabel != null) promptLabel.text = action.GetBindingDisplayString(bindingIndex);
    }

    private void SaveOverrides()
    {
        string key = PrefsKey();
        if (string.IsNullOrEmpty(key)) return;
        PlayerPrefs.SetString(key, actions.SaveBindingOverridesAsJson());
        PlayerPrefs.Save();
    }

    private string PrefsKey() => actions != null ? $"MCPRebind_{actions.name}" : null;
}
"""


@workflow(
    "add_rebinding_ui",
    "Attaches a scaffolded MCPRebindButton to a UI Button: on click, starts an interactive rebind for one action's "
    "binding via InputActionRebindingExtensions.PerformInteractiveRebinding (the real, documented rebinding API), "
    "persists overrides to PlayerPrefs across sessions, and updates an optional label with the new binding's "
    "display string. Manual Test: interactive rebinding needs a real input device press to complete, which isn't "
    "observable in an automated/headless invoke test.",
    {
        "type": "object",
        "properties": {
            "buttonPath": {"type": "string", "description": "Hierarchy path of an existing UI Button GameObject to attach the rebind behavior to."},
            "actionsAssetPath": {"type": "string", "description": "Path relative to Assets/ of the .inputactions asset containing the action to rebind."},
            "mapName": {"type": "string", "description": "Name of the action map containing the action."},
            "actionName": {"type": "string", "description": "Name of the action to rebind."},
            "bindingIndex": {"type": "number", "description": "Index of the binding within the action to rebind. Defaults to 0."},
            "labelPath": {"type": "string", "description": "Hierarchy path of a UI Text to show the current binding/rebind prompt. Omit to skip."},
        },
        "required": ["buttonPath", "actionsAssetPath", "mapName", "actionName"],
    },
    group="input",
)
async def _add_rebinding_ui(bridge: UnityBridgeClient, args: dict) -> Any:
    button_path = args["buttonPath"]
    actions_asset_path = args["actionsAssetPath"]
    map_name = args["mapName"]
    action_name = args["actionName"]
    binding_index = int(args.get("bindingIndex", 0))
    label_path = args.get("labelPath")

    created = await _scaffold_script(bridge, _REBIND_BUTTON_PATH, _REBIND_BUTTON_CONTENT)
    if created:
        await _wait_for_compile(bridge)

    await bridge.call("add_component", {"path": button_path, "typeName": "MCPRebindButton"})
    await bridge.call("wire_object_reference", {
        "path": button_path, "typeName": "MCPRebindButton", "fieldName": "actions", "targetAssetPath": actions_asset_path,
    })
    await _apply_field_batch(bridge, button_path, "MCPRebindButton", {
        "actionMapName": map_name, "actionName": action_name, "bindingIndex": binding_index,
    }, ["actionMapName", "actionName", "bindingIndex"])

    if label_path:
        await bridge.call("wire_object_reference", {
            "path": button_path, "typeName": "MCPRebindButton", "fieldName": "promptLabel", "targetGameObjectPath": label_path,
        })

    await bridge.call("wire_unity_event", {
        "path": button_path, "typeName": "Button", "eventFieldName": "onClick",
        "targetPath": button_path, "targetTypeName": "MCPRebindButton", "methodName": "StartRebind",
    })

    return {"path": button_path}


# Imported for side effects only -- registers any hand-written composite tools defined in
# custom_workflows.py. Imported at the very end of this file, after `workflow` (the
# decorator) is fully defined, since custom_workflows.py imports it back via
# `from .workflows import workflow`.
from . import custom_workflows  # noqa: F401,E402
