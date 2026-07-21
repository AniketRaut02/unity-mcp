#!/usr/bin/env python3
"""
Extracts the actual Behavior Tree framework C# content from
python/unity_mcp_server/workflows.py and compiles it with mono's `mcs` against
the same lightweight Unity API stubs the rest of dev-tests/csharp uses.

This is checking the REAL embedded strings that scaffold_behavior_tree_framework
writes to disk in production -- not a copy, not a paraphrase -- so a typo or a
real C# error in that content gets caught here instead of only being discovered
the first time someone actually runs the workflow inside Unity.

Requires: mono-mcs (`apt-get install mono-mcs` on Debian/Ubuntu, or the Mono
distribution for your platform). Does NOT require Unity to be installed.
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO_ROOT / "python"))

from unity_mcp_server import workflows  # noqa: E402

STUB_PATH = REPO_ROOT / "dev-tests" / "csharp" / "stubs" / "UnityStubs.cs"


def main() -> int:
    with tempfile.TemporaryDirectory() as tmp:
        src_files = [str(STUB_PATH)]
        for relative_path, content in workflows._BT_FRAMEWORK_FILES.items():
            file_name = relative_path.rsplit("/", 1)[-1]
            out_path = os.path.join(tmp, file_name)
            with open(out_path, "w") as f:
                f.write(content)
            src_files.append(out_path)
            print(f"Extracted {relative_path} ({len(content)} bytes)")

        dll_path = os.path.join(tmp, "BehaviorTree.dll")
        result = subprocess.run(
            ["mcs", "-target:library", f"-out:{dll_path}"] + src_files,
            capture_output=True,
            text=True,
        )

        print(result.stdout)
        if result.returncode != 0:
            print(result.stderr, file=sys.stderr)
            print("\n[FAIL] Behavior Tree framework content does not compile.")
            return 1

        print("\n[PASS] Behavior Tree framework content compiles cleanly.")
        return 0


if __name__ == "__main__":
    sys.exit(main())
