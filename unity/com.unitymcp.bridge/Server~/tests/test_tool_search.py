"""
Unit tests for the BM25 tool-search index (unity_mcp_server/tool_search.py). Pure
algorithm tests against hand-built ToolDocs -- no bridge, no Unity, no async -- since
search scoring is deterministic, self-contained logic. See test_manage_tools_search.py
for the manage_tools(action="search")/activate([...])/soft-budget-guard integration
tests that exercise the real workflow handler.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from unity_mcp_server import tool_search  # noqa: E402


def _names(docs):
    return [d.name for d in docs]


def test_exact_name_match_ranks_first():
    docs = [
        tool_search.ToolDoc(name="raycast", group="physics", description="Casts a ray and returns the first hit, if any."),
        tool_search.ToolDoc(name="spherecast", group="physics", description="Casts a sphere along a direction and returns the first hit, if any."),
        tool_search.ToolDoc(name="overlap_query", group="physics", description="Returns every collider overlapping a Sphere or Box volume."),
    ]
    index = tool_search.ToolSearchIndex(docs)
    hits = index.search("raycast")
    assert hits and hits[0].name == "raycast", _names(hits)
    print("[PASS] an exact tool-name query ranks that tool first")


def test_word_form_mismatch_still_matches_via_stemming():
    # The real motivating case: a natural-language query using "flickering" must still
    # find a tool named with "flicker" -- confirmed this fails without stemming, fixed
    # by tool_search._stem, before this test existed.
    docs = [
        tool_search.ToolDoc(name="add_flicker_light", group="lighting", description="Attaches a scaffolded MCPFlickerLight component to a Light for horror-style flicker."),
        tool_search.ToolDoc(name="raycast", group="physics", description="Casts a ray and returns the first hit, if any."),
    ]
    index = tool_search.ToolSearchIndex(docs)
    hits = index.search("flickering light")
    assert hits and hits[0].name == "add_flicker_light", _names(hits)
    print("[PASS] a query using a different word form ('flickering') still matches a tool named with the root form ('flicker')")


def test_name_field_outranks_description_only_match():
    docs = [
        tool_search.ToolDoc(name="sculpt_terrain_height", group="terrain", description="Raises/lowers/flattens terrain heights within a circular brush, linear falloff at the edge."),
        tool_search.ToolDoc(name="paint_terrain_texture", group="terrain", description="Paints alphamap weight for one terrain layer within a circular brush."),
    ]
    index = tool_search.ToolSearchIndex(docs)
    hits = index.search("terrain sculpting")
    assert hits[0].name == "sculpt_terrain_height", _names(hits)
    print("[PASS] a query matching the tool's own name ranks above one only matching another tool's description")


def test_group_pseudo_document_surfaces_for_broad_queries():
    docs = [
        tool_search.ToolDoc(name="create_timeline", group="timeline", description="Creates a new TimelineAsset."),
        tool_search.ToolDoc(name="add_timeline_signal", group="timeline", description="Adds a SignalEmitter marker to a Signal track."),
        tool_search.ToolDoc(group="timeline", description="Timeline assets, tracks/clips, signals, track bindings, camera-cut tracks, scripted scare sequences."),
        tool_search.ToolDoc(name="raycast", group="physics", description="Casts a ray and returns the first hit, if any."),
    ]
    index = tool_search.ToolSearchIndex(docs)
    hits = index.search("scripted scare sequences")
    assert any(d.name is None and d.group == "timeline" for d in hits), _names(hits)
    print("[PASS] a group's own description is indexed and can surface as a group-level hit for a broad query")


def test_no_match_returns_empty():
    docs = [tool_search.ToolDoc(name="raycast", group="physics", description="Casts a ray and returns the first hit, if any.")]
    index = tool_search.ToolSearchIndex(docs)
    assert index.search("xyzzy nonsense query") == []
    print("[PASS] a query with no matching terms returns no results, not an error or garbage ranking")


def test_limit_is_respected():
    docs = [tool_search.ToolDoc(name=f"light_tool_{i}", group="lighting", description="A light-related tool for testing limit behavior.") for i in range(20)]
    index = tool_search.ToolSearchIndex(docs)
    hits = index.search("light", limit=3)
    assert len(hits) == 3, len(hits)
    print("[PASS] search respects the limit parameter")


def test_empty_query_returns_empty():
    docs = [tool_search.ToolDoc(name="raycast", group="physics", description="Casts a ray.")]
    index = tool_search.ToolSearchIndex(docs)
    assert tool_search.ToolSearchIndex(docs).search("") == []
    assert tool_search.ToolSearchIndex(docs).search("   ") == []
    print("[PASS] an empty/whitespace-only query returns no results rather than matching everything")


def main():
    test_exact_name_match_ranks_first()
    test_word_form_mismatch_still_matches_via_stemming()
    test_name_field_outranks_description_only_match()
    test_group_pseudo_document_surfaces_for_broad_queries()
    test_no_match_returns_empty()
    test_limit_is_respected()
    test_empty_query_returns_empty()
    print("\nAll tool_search BM25 index checks passed.")


if __name__ == "__main__":
    main()
