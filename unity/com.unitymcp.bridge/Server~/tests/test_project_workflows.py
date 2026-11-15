"""
Project composite tools: <project>/UnityMCP/custom_workflows.py is loaded at server start.

  - A file with no import line still works (`workflow` is pre-injected), which is what the
    Tool Builder writes on first use.
  - A file with its own `from unity_mcp_server.workflows import workflow` works too.
  - A broken file is logged and skipped, never crashing the server.
  - No file at all is fine.
"""
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from unity_mcp_server import workflows  # noqa: E402


def project_with(source: str) -> Path:
    root = Path(tempfile.mkdtemp())
    (root / "UnityMCP").mkdir()
    (root / "UnityMCP" / "custom_workflows.py").write_text(source, encoding="utf-8")
    return root


def main():
    bare = project_with('''
@workflow("project_tool_bare", "A project tool.", {"type": "object", "properties": {}}, group="core")
async def _bare(bridge, args):
    return "bare"
''')
    assert workflows.load_project_workflows(bare) is not None
    assert workflows.get_workflow("project_tool_bare") is not None
    print("[PASS] a project file without an import line registers its tool")

    imported = project_with('''
from unity_mcp_server.workflows import workflow

@workflow("project_tool_imported", "Another.", {"type": "object", "properties": {}}, group="core")
async def _imported(bridge, args):
    return "imported"
''')
    assert workflows.load_project_workflows(imported) is not None
    assert workflows.get_workflow("project_tool_imported") is not None
    print("[PASS] a project file with its own import registers its tool")

    broken = project_with("this is not python(")
    assert workflows.load_project_workflows(broken) is None
    print("[PASS] a broken project file is skipped, not fatal")

    empty = Path(tempfile.mkdtemp())
    assert workflows.load_project_workflows(empty) is None
    print("[PASS] no project file at all is fine")

    for root in (bare, imported, broken, empty):
        shutil.rmtree(root, ignore_errors=True)
    print("\nAll project-workflow loading checks passed.")


if __name__ == "__main__":
    main()
