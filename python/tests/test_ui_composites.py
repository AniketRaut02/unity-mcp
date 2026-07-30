"""
Real-logic tests for the ui-group composites: create_health_bar, create_ammo_counter,
create_crosshair, create_interaction_prompt, create_pause_menu, create_subtitle_system.
"""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from unity_mcp_server import workflows  # noqa: E402
from unity_mcp_server.bridge_client import BridgeError  # noqa: E402


class FakeBridge:
    def __init__(self):
        self.calls = []
        self.scripts_created = set()
        self._name_counters = {}

    async def call(self, tool: str, args: dict):
        self.calls.append((tool, dict(args)))

        if tool == "create_ui_element":
            n = self._name_counters.get(args["name"], 0)
            self._name_counters[args["name"]] = n + 1
            parent = args.get("parentPath")
            leaf = args["name"]
            return {"path": f"{parent}/{leaf}" if parent else leaf}
        if tool in ("set_rect_transform", "set_ui_color", "set_layout", "set_gameobject_active",
                    "set_component_properties_batch", "add_component", "wire_object_reference", "wire_unity_event"):
            return None
        if tool == "create_script":
            path = args["path"]
            if path in self.scripts_created:
                raise BridgeError(f"'{path}' already exists. Use update_script to modify it.")
            self.scripts_created.add(path)
            return {"path": path}
        if tool == "update_script":
            return None
        if tool == "get_compile_status":
            return {"isCompiling": False, "errorCount": 0, "errors": []}

        raise AssertionError(f"FakeBridge got an unexpected tool call: {tool}")


async def test_create_health_bar():
    create_health_bar = workflows.get_workflow("create_health_bar").handler

    bridge = FakeBridge()
    result = await create_health_bar(bridge, {"path": "Canvas", "width": 250, "height": 25})
    assert result == {"path": "Canvas/HealthBar", "fillPath": "Canvas/HealthBar/Fill"}, result

    bg_call = next(a for t, a in bridge.calls if t == "create_ui_element" and a["name"] == "HealthBar")
    assert bg_call["type"] == "Panel" and bg_call["parentPath"] == "Canvas", bg_call
    fill_call = next(a for t, a in bridge.calls if t == "create_ui_element" and a["name"] == "Fill")
    assert fill_call["type"] == "Image" and fill_call["parentPath"] == "Canvas/HealthBar", fill_call

    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch")
    assert batch_call["path"] == "Canvas/HealthBar/Fill" and batch_call["fieldNames"] == ["type", "fillMethod", "fillAmount"], batch_call
    assert batch_call["values"] == ["Filled", "Horizontal", "1"], batch_call

    assert "Scripts/MCP/MCPValueBarUI.cs" in bridge.scripts_created
    wire_call = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert wire_call["fieldName"] == "targetImage" and wire_call["targetGameObjectPath"] == "Canvas/HealthBar/Fill", wire_call
    print("[PASS] create_health_bar builds a background+Fill Image pair, configures Filled/Horizontal, and wires MCPValueBarUI to the real Fill image")


async def test_create_ammo_counter():
    create_ammo_counter = workflows.get_workflow("create_ammo_counter").handler

    bridge = FakeBridge()
    result = await create_ammo_counter(bridge, {"path": "Canvas"})
    assert result == {"path": "Canvas/AmmoCounter"}, result

    create_call = next(a for t, a in bridge.calls if t == "create_ui_element")
    assert create_call["type"] == "Text" and create_call["name"] == "AmmoCounter", create_call
    assert "Scripts/MCP/MCPAmmoCounterUI.cs" in bridge.scripts_created
    wire_call = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert wire_call["fieldName"] == "targetText" and wire_call["targetGameObjectPath"] == "Canvas/AmmoCounter", wire_call
    print("[PASS] create_ammo_counter creates a Text readout and wires MCPAmmoCounterUI to itself")


async def test_create_crosshair():
    create_crosshair = workflows.get_workflow("create_crosshair").handler

    bridge = FakeBridge()
    result = await create_crosshair(bridge, {"path": "Canvas", "baseSize": 6, "maxSpread": 30})
    assert result == {"path": "Canvas/Crosshair"}, result

    rect_call = next(a for t, a in bridge.calls if t == "set_rect_transform")
    assert rect_call["sizeX"] == 6 and rect_call["sizeY"] == 6, rect_call

    assert "Scripts/MCP/MCPCrosshairUI.cs" in bridge.scripts_created
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch" and a["typeName"] == "MCPCrosshairUI")
    assert batch_call["fieldNames"] == ["baseSize", "maxSpread"] and batch_call["values"] == ["6", "30"], batch_call
    print("[PASS] create_crosshair creates a dot Image sized to baseSize and configures MCPCrosshairUI's real spread range")


async def test_create_interaction_prompt():
    create_prompt = workflows.get_workflow("create_interaction_prompt").handler

    bridge = FakeBridge()
    result = await create_prompt(bridge, {"path": "Canvas", "raycasterPath": "Player"})
    assert result == {"path": "Canvas/InteractionPrompt"}, result

    active_call = next(a for t, a in bridge.calls if t == "set_gameobject_active")
    assert active_call == {"path": "Canvas/InteractionPrompt", "active": False}, active_call

    assert "Scripts/MCP/MCPInteractionPromptUI.cs" in bridge.scripts_created

    wire_calls = [a for t, a in bridge.calls if t == "wire_unity_event"]
    assert len(wire_calls) == 2, wire_calls
    found_call = next(c for c in wire_calls if c["eventFieldName"] == "onInteractableFound")
    assert found_call["path"] == "Player" and found_call["typeName"] == "MCPInteractionRaycaster", found_call
    assert found_call["targetPath"] == "Canvas/InteractionPrompt" and found_call["methodName"] == "Show", found_call
    lost_call = next(c for c in wire_calls if c["eventFieldName"] == "onInteractableLost")
    assert lost_call["methodName"] == "Hide", lost_call
    print("[PASS] create_interaction_prompt starts hidden and really wires onInteractableFound/onInteractableLost via wire_unity_event")

    bridge2 = FakeBridge()
    await create_prompt(bridge2, {"path": "Canvas"})
    assert not any(t == "wire_unity_event" for t, _ in bridge2.calls)
    print("[PASS] create_interaction_prompt without raycasterPath skips wiring entirely")


async def test_create_pause_menu():
    create_pause_menu = workflows.get_workflow("create_pause_menu").handler

    bridge = FakeBridge()
    result = await create_pause_menu(bridge, {"path": "Canvas"})
    assert result == {"path": "Canvas/PauseMenu", "controllerPath": "Canvas"}, result

    panel_active_call = next(a for t, a in bridge.calls if t == "set_gameobject_active")
    assert panel_active_call == {"path": "Canvas/PauseMenu", "active": False}, panel_active_call

    button_creates = [a for t, a in bridge.calls if t == "create_ui_element" and a["type"] == "Button"]
    assert [c["name"] for c in button_creates] == ["ResumeButton", "QuitButton"], button_creates

    assert "Scripts/MCP/MCPPauseMenuUI.cs" in bridge.scripts_created
    panel_wire = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert panel_wire["path"] == "Canvas" and panel_wire["fieldName"] == "panel" and panel_wire["targetGameObjectPath"] == "Canvas/PauseMenu", panel_wire

    event_wires = [a for t, a in bridge.calls if t == "wire_unity_event"]
    resume_wire = next(c for c in event_wires if c["methodName"] == "Resume")
    assert resume_wire["path"] == "Canvas/PauseMenu/ResumeButton" and resume_wire["typeName"] == "Button", resume_wire
    quit_wire = next(c for c in event_wires if c["methodName"] == "Quit")
    assert quit_wire["path"] == "Canvas/PauseMenu/QuitButton", quit_wire
    print("[PASS] create_pause_menu builds a hidden panel with Resume/Quit buttons really wired to MCPPauseMenuUI via wire_unity_event")


async def test_create_subtitle_system():
    create_subtitles = workflows.get_workflow("create_subtitle_system").handler

    bridge = FakeBridge()
    result = await create_subtitles(bridge, {"path": "Canvas", "displayDuration": 5})
    assert result == {"path": "Canvas/Subtitles"}, result

    active_call = next(a for t, a in bridge.calls if t == "set_gameobject_active")
    assert active_call == {"path": "Canvas/Subtitles", "active": False}, active_call

    assert "Scripts/MCP/MCPSubtitleUI.cs" in bridge.scripts_created
    wire_call = next(a for t, a in bridge.calls if t == "wire_object_reference")
    assert wire_call["fieldName"] == "subtitleText" and wire_call["targetGameObjectPath"] == "Canvas/Subtitles", wire_call
    batch_call = next(a for t, a in bridge.calls if t == "set_component_properties_batch" and a["typeName"] == "MCPSubtitleUI")
    assert batch_call["fieldNames"] == ["displayDuration"] and batch_call["values"] == ["5"], batch_call
    print("[PASS] create_subtitle_system starts hidden, wires its own Text, and batches displayDuration")


async def main():
    await test_create_health_bar()
    await test_create_ammo_counter()
    await test_create_crosshair()
    await test_create_interaction_prompt()
    await test_create_pause_menu()
    await test_create_subtitle_system()
    print("\nAll ui-group composite-logic checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
