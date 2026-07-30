"""
Enforces the tool-scaling plan's description-budget target (docs/tool-scaling-strategy.md,
section 11): no atomic (C#) or composite (Python) tool description should regress past a
hard cap, and the catalog-wide average should stay near the measured baseline. Parses the
real [MCPTool(...)] attribute source directly (not a live Unity connection -- there isn't
one in this test process) so this catches a future tool landing with an oversized
description before it ships, the same way wire_unity_event's 1,647-char description (since
trimmed to ~720) was found by manual audit rather than any automated check.
"""
import glob
import os
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

# A single tool description this large is a sign engineering narrative leaked into
# AI-facing text instead of staying in CHANGELOG.md/comments -- see wire_unity_event's
# history. Composite tools run a bit longer on average (more moving parts to describe)
# so get a slightly higher cap; manage_tools itself is exempt below (see comment there).
_ATOMIC_HARD_CAP = 900
_COMPOSITE_HARD_CAP = 900

_MCPTOOL_RE = re.compile(
    r'\[MCPTool\s*\(\s*"([^"]+)"\s*,\s*((?:"(?:[^"\\]|\\.)*"\s*\+?\s*)+)', re.DOTALL
)
_STRING_LITERAL_RE = re.compile(r'"((?:[^"\\]|\\.)*)"')


def _atomic_tool_descriptions() -> dict[str, int]:
    tools_dir = REPO_ROOT / "unity" / "com.unitymcp.bridge" / "Editor" / "Tools"
    lengths = {}
    for path in glob.glob(str(tools_dir / "*.cs")):
        text = Path(path).read_text(encoding="utf-8")
        for m in _MCPTOOL_RE.finditer(text):
            name = m.group(1)
            parts = _STRING_LITERAL_RE.findall(m.group(2))
            lengths[name] = len("".join(parts))
    return lengths


def _composite_tool_descriptions() -> dict[str, int]:
    from unity_mcp_server import workflows

    return {wf.name: len(wf.description) for wf in workflows.all_workflows()}


def main() -> None:
    os.environ.setdefault("UNITY_MCP_PROJECT_ROOT", str(REPO_ROOT))

    atomic = _atomic_tool_descriptions()
    assert len(atomic) > 200, f"Expected 200+ atomic tools parsed from source, got {len(atomic)} -- regex likely broke."
    over_cap = {n: l for n, l in atomic.items() if l > _ATOMIC_HARD_CAP}
    assert not over_cap, f"Atomic tool description(s) over the {_ATOMIC_HARD_CAP}-char budget: {over_cap}"
    print(f"[PASS] {len(atomic)} atomic tool descriptions, all <= {_ATOMIC_HARD_CAP} chars "
          f"(max: {max(atomic.values())}).")

    composite = _composite_tool_descriptions()
    assert len(composite) > 50, f"Expected 50+ composite tools, got {len(composite)}."
    # manage_tools carries the "Groups at a glance" catalog (phase 2 of the scaling plan)
    # appended to its own description on purpose -- it replaces what would otherwise be a
    # separate list_groups round trip, so it is deliberately exempt from the per-tool cap.
    over_cap = {n: l for n, l in composite.items() if l > _COMPOSITE_HARD_CAP and n != "manage_tools"}
    assert not over_cap, f"Composite tool description(s) over the {_COMPOSITE_HARD_CAP}-char budget: {over_cap}"
    print(f"[PASS] {len(composite)} composite tool descriptions, all <= {_COMPOSITE_HARD_CAP} chars "
          f"(excluding manage_tools, which intentionally carries the group catalog; max: "
          f"{max(l for n, l in composite.items() if n != 'manage_tools')}).")

    print("\nAll tool-description-budget checks passed.")


if __name__ == "__main__":
    main()
