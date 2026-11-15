"""
The Unity package and this server ship together and must carry one version number:
package.json, pyproject.toml, the server's own SERVER_VERSION and the Claude Desktop
bundle's manifest.json. The release workflow
also checks the git tag against package.json.
"""
import json
import re
import sys
from pathlib import Path

SERVER_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SERVER_DIR))

from unity_mcp_server.bridge_client import SERVER_VERSION  # noqa: E402


def main():
    package_version = json.loads((SERVER_DIR.parent / "package.json").read_text(encoding="utf-8"))["version"]
    pyproject_version = re.search(r'^version = "([^"]+)"', (SERVER_DIR / "pyproject.toml").read_text(encoding="utf-8"), re.M).group(1)
    bundle_version = json.loads((SERVER_DIR / "manifest.json").read_text(encoding="utf-8"))["version"]
    assert package_version == pyproject_version == SERVER_VERSION == bundle_version, (
        package_version, pyproject_version, SERVER_VERSION, bundle_version)
    print(f"[PASS] package.json, pyproject.toml, SERVER_VERSION and the .mcpb manifest all say {package_version}")


if __name__ == "__main__":
    main()
