# Releasing

One version number covers everything: the Unity package, the Python server (PyPI:
`darkpixel-unity-mcp`) and the Claude Desktop bundle. `tests/test_versions.py` fails if they
drift, and the release workflow refuses a tag that doesn't match `package.json`.

## Every release

1. Set the new version in all four places:
   - `unity/com.unitymcp.bridge/package.json` (`version`)
   - `unity/com.unitymcp.bridge/Server~/pyproject.toml` (`version`)
   - `unity/com.unitymcp.bridge/Server~/manifest.json` (`version`)
   - `unity/com.unitymcp.bridge/Server~/unity_mcp_server/bridge_client.py` (`SERVER_VERSION`)

   Bump `PROTOCOL_VERSION` (bridge_client.py) and `MCPBridge.ProtocolVersion` (C#) together,
   and only when the Unity-to-server wire format changes.
2. If you changed dependencies in `pyproject.toml`, run `uv lock` in `Server~` and commit `uv.lock`.
3. Add a `CHANGELOG.md` entry.
4. Run the tests: in `Server~`, `uv run --frozen python tests/<file>.py` for each test file.
5. Commit, then tag and push:
   ```
   git tag v2.0.0
   git push origin main v2.0.0
   ```
   `.github/workflows/release.yml` then:
   - runs every server test;
   - publishes to PyPI;
   - creates a GitHub Release with `unity-mcp-v2.0.0.mcpb` attached.
6. OpenUPM notices the new tag on its own within a few hours (after the one-time setup below).

## One-time setup

### PyPI (trusted publishing, no token stored anywhere)
1. Create a pypi.org account with 2FA.
2. Go to *Your account > Publishing > Add a new pending publisher* and enter:
   - PyPI project name: `darkpixel-unity-mcp`
   - Owner: `AniketRaut02`, Repository: `unity-mcp`
   - Workflow name: `release.yml`, Environment name: `pypi`
3. In the GitHub repository go to *Settings > Environments > New environment* and name it `pypi`.
   Optionally add yourself as a required reviewer, so every publish waits for your click.

### OpenUPM
1. Submit the package at https://openupm.com/packages/add/ with the repository URL.
2. The package lives in a subfolder (`unity/com.unitymcp.bridge`), so check during submission
   that OpenUPM picks up that `package.json`; the form shows what it found.
3. After approval, users install with `openupm add com.darkpixelgd.unitymcpbridge`.

### Before the first public .mcpb
Install the bundle in Claude Desktop once (double-click it), pick a Unity project folder, and
confirm the Unity tools appear. The manifest uses the `uv` server type (manifest 0.4), which is
newer than the other types: check it end to end once before announcing it.
