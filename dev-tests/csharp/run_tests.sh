#!/bin/bash
# Compiles and runs the Phase 2 C# logic tests (destructive/confirm gate, rate
# limiter, path guard) against lightweight Unity API stubs, using mono's `mcs`
# compiler and runtime. This does NOT require Unity to be installed.
#
# What this is: a fast way to verify registry/security *logic* changes without
# opening the Editor. It links the actual production .cs files, not copies.
# What this is NOT: a substitute for testing inside the real Unity Editor —
# the stubs are intentionally minimal and don't reproduce Unity's real behavior
# for anything beyond what these specific tests touch.
#
# Requires: mono-mcs and mono-runtime (`apt-get install mono-mcs mono-runtime`
# on Debian/Ubuntu, or the Mono distribution for your platform).
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UNITY_SRC="$SCRIPT_DIR/../../unity/com.unitymcp.bridge/Editor"
OUT="/tmp/unity_mcp_csharp_tests.exe"

mcs -target:exe -out:"$OUT" \
  "$SCRIPT_DIR/stubs/UnityStubs.cs" \
  "$SCRIPT_DIR/RegistryTests.cs" \
  "$UNITY_SRC/Protocol/MCPMessage.cs" \
  "$UNITY_SRC/MCPToolAttribute.cs" \
  "$UNITY_SRC/MCPParamAttribute.cs" \
  "$UNITY_SRC/MCPResult.cs" \
  "$UNITY_SRC/MCPToolContext.cs" \
  "$UNITY_SRC/MCPToolRegistry.cs" \
  "$UNITY_SRC/MCPProjectUtil.cs" \
  "$UNITY_SRC/Security/MCPAuthHandshake.cs" \
  "$UNITY_SRC/Security/MCPSessionFile.cs" \
  "$UNITY_SRC/Security/MCPInstanceConflictDetector.cs" \
  "$UNITY_SRC/Security/MCPInstanceLock.cs" \
  "$UNITY_SRC/Security/MCPRateLimiter.cs" \
  "$UNITY_SRC/Security/MCPPathGuard.cs" \
  "$UNITY_SRC/Security/MCPAuditLog.cs" \
  "$UNITY_SRC/MCPServer.cs" \
  "$UNITY_SRC/MCPCommandDispatcher.cs" \
  "$UNITY_SRC/Tools/MCPSceneUtil.cs" \
  "$UNITY_SRC/Tools/MCPTypeResolver.cs" \
  "$UNITY_SRC/Tools/MCPHierarchyCache.cs" \
  "$UNITY_SRC/Tools/SceneTools.cs" \
  "$UNITY_SRC/Tools/ComponentTools.cs" \
  "$UNITY_SRC/Tools/MCPConsoleCapture.cs" \
  "$UNITY_SRC/Tools/QueryTools.cs" \
  "$UNITY_SRC/Tools/MCPCompileStatus.cs" \
  "$UNITY_SRC/Tools/MCPScriptTemplates.cs" \
  "$UNITY_SRC/Tools/ScriptingTools.cs" \
  "$UNITY_SRC/Tools/PhysicsTools.cs" \
  "$UNITY_SRC/Tools/AssetTools.cs" \
  "$UNITY_SRC/Tools/UITools.cs" \
  "$UNITY_SRC/Setup/MCPClientDetector.cs" \
  "$UNITY_SRC/Setup/MCPMiniJson.cs" \
  "$UNITY_SRC/Setup/MCPMcpServersJsonWriter.cs" \
  "$UNITY_SRC/Setup/MCPCodexTomlWriter.cs" \
  "$UNITY_SRC/Setup/MCPServerEntryBuilder.cs" \
  "$UNITY_SRC/Setup/MCPMcpConfigTargets.cs" \
  "$UNITY_SRC/Setup/MCPPythonServerSettings.cs" \
  "$UNITY_SRC/Setup/MCPClientConfigTracker.cs" \
  "$UNITY_SRC/Setup/MCPSetupWindow.cs"

mono "$OUT"
