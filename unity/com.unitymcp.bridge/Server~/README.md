# darkpixel-unity-mcp

The MCP server half of [Unity MCP Bridge](https://github.com/AniketRaut02/unity-mcp): it gives
Claude Code, Codex, Cursor, VS Code, Claude Desktop and other MCP clients 300+ tools inside a
running Unity Editor.

**You normally don't install this by hand.** It ships inside the Unity package
(`com.darkpixelgd.unitymcpbridge`), and the Unity Setup window (Window > Unity MCP > Setup)
configures your MCP client to run it with [uv](https://docs.astral.sh/uv/).

To run it directly (for example from a client config you write yourself):

```
uvx darkpixel-unity-mcp
```

with `UNITY_MCP_PROJECT_ROOT` set to your Unity project folder, or started from inside it.
The Unity package and this server must be the same release (they check on connect).

See the main repository for documentation, security notes and the changelog.
