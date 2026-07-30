// Standalone executable test harness. Links the ACTUAL production classes
// (MCPToolRegistry, MCPRateLimiter, MCPPathGuard, MCPToolAttribute) against the
// Unity API stubs, so this exercises real Phase 2 logic end-to-end, not a mock of it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityMCP;
using UnityMCP.Protocol;
using UnityMCP.Security;
using UnityMCP.Setup;
using UnityMCP.Tools;

public static class TestTool
{
    [MCPTool("test_delete", "Test-only destructive tool.", MCPLatencyTier.Fast, destructive: true)]
    public static MCPResult TestDelete(MCPToolContext ctx, string path)
    {
        // Deliberately distinct from the confirm-check failure message so the test can
        // tell "confirm gate blocked it" apart from "the method actually ran".
        return MCPResult.Fail($"METHOD_RAN: path '{path}' not found (expected in stub environment).");
    }

    [MCPTool("test_nondestructive", "Test-only non-destructive tool.")]
    public static MCPResult TestNonDestructive(MCPToolContext ctx, string value)
    {
        return MCPResult.Success(new { echoed = value });
    }

    [MCPTool("test_slow", "Test-only Slow-tier tool, used to verify batch rejection and queue prioritization.", MCPLatencyTier.Slow)]
    public static MCPResult TestSlow(MCPToolContext ctx)
    {
        return MCPResult.Success();
    }

    [MCPTool("test_string_array", "Test-only tool taking a required string[] and an optional one, used to verify array-parameter schema generation and wire coercion.")]
    public static MCPResult TestStringArray(
        MCPToolContext ctx,
        [MCPParam("Required list of tags.")] string[] tags,
        [MCPParam("Optional list of extra names.")] string[] extras = null)
    {
        return MCPResult.Success(new { tagCount = tags?.Length ?? 0, tags, extraCount = extras?.Length ?? 0 });
    }

    // Matches docs/writing-custom-tools.md's own §1 example verbatim, so the guide's
    // claim "here's the whole tool, it just works" is actually checked, not just written.
    [MCPTool("spawn_coin", "Instantiates a coin pickup prefab at a world position.")]
    public static MCPResult SpawnCoin(
        MCPToolContext ctx,
        [MCPParam("World-space X position.")] float x,
        [MCPParam("World-space Y position.")] float y,
        [MCPParam("World-space Z position.")] float z)
    {
        var go = new UnityEngine.GameObject("Coin");
        go.transform.position = new UnityEngine.Vector3(x, y, z);
        UnityEditor.Undo.RegisterCreatedObjectUndo(go, "MCP: Spawn Coin");

        return MCPResult.Success(new { path = MCPSceneUtil.GetPath(go) });
    }
}

// Test-only ScriptableObject subclass so create_scriptable_object has something
// real to instantiate — a stand-in for a user script created via
// create_script(template=ScriptableObject) in actual use.
public class TestScriptableObjectClass : UnityEngine.ScriptableObject
{
    public int testField = 7;
}

// Two types sharing a short name across different namespaces, purely to give
// TestTypeResolverCachingAndAmbiguity something real to detect as ambiguous --
// exactly the "your own Enemy class vs. a package's Enemy class" scenario the fix
// is meant to catch, reproduced deliberately rather than hoped-for.
namespace UnityMCP.TestNamespaceA
{
    public class AmbiguousTestType {}
}

namespace UnityMCP.TestNamespaceB
{
    public class AmbiguousTestType {}
}

public static class Program
{
    private static int _failures = 0;

    private static void Check(bool condition, string message)
    {
        if (condition)
        {
            Console.WriteLine("[PASS] " + message);
        }
        else
        {
            Console.WriteLine("[FAIL] " + message);
            _failures++;
        }
    }

    public static int Main()
    {
        MCPToolRegistry.Rescan();

        // --- Destructive/confirm gate ---
        var ctx = new MCPToolContext { RequestId = "test-1" };

        var noConfirm = MCPToolRegistry.Invoke("test_delete", new Dictionary<string, object> { ["path"] = "Foo" }, ctx);
        Check(!noConfirm.Ok, "test_delete without confirm is rejected");
        Check(noConfirm.Error != null && noConfirm.Error.Contains("requires confirm=true"),
            "rejection message names the confirm requirement, method body did not run: " + noConfirm.Error);

        var withConfirmFalse = MCPToolRegistry.Invoke(
            "test_delete", new Dictionary<string, object> { ["path"] = "Foo", ["confirm"] = false }, ctx);
        Check(!withConfirmFalse.Ok, "test_delete with confirm=false (explicit) is still rejected");

        var withConfirmTrue = MCPToolRegistry.Invoke(
            "test_delete", new Dictionary<string, object> { ["path"] = "Foo", ["confirm"] = true }, ctx);
        Check(!withConfirmTrue.Ok && withConfirmTrue.Error != null && withConfirmTrue.Error.StartsWith("METHOD_RAN"),
            "test_delete with confirm=true actually invokes the method body: " + withConfirmTrue.Error);

        // Confirm that the synthetic 'confirm' key never reaches the method as a real
        // argument (it has no 'confirm' parameter at all — if binding tried to pass it
        // positionally this would have thrown during Invoke already).
        Check(MCPToolRegistry.Tools["test_delete"].Schema.ContainsKey("properties"), "schema was generated for test_delete");
        var schema = (Dictionary<string, object>)MCPToolRegistry.Tools["test_delete"].Schema["properties"];
        Check(schema.ContainsKey("confirm"), "schema for a destructive tool includes a synthetic 'confirm' property");
        var required = (List<string>)MCPToolRegistry.Tools["test_delete"].Schema["required"];
        Check(required.Contains("confirm"), "'confirm' is marked required in the schema");

        // Non-destructive tool: no confirm needed, no confirm in schema.
        var normalCall = MCPToolRegistry.Invoke(
            "test_nondestructive", new Dictionary<string, object> { ["value"] = "hi" }, ctx);
        Check(normalCall.Ok, "non-destructive tool runs without any confirm argument");
        var normalSchema = (Dictionary<string, object>)MCPToolRegistry.Tools["test_nondestructive"].Schema["properties"];
        Check(!normalSchema.ContainsKey("confirm"), "non-destructive tool's schema has no synthetic 'confirm' property");

        // --- Rate limiter ---
        var limiter = new MCPRateLimiter(maxPerWindow: 3, window: TimeSpan.FromMilliseconds(200));
        Check(limiter.TryAcquire(), "rate limiter allows call 1/3");
        Check(limiter.TryAcquire(), "rate limiter allows call 2/3");
        Check(limiter.TryAcquire(), "rate limiter allows call 3/3");
        Check(!limiter.TryAcquire(), "rate limiter blocks call 4 within the same window");
        Thread.Sleep(250);
        Check(limiter.TryAcquire(), "rate limiter allows a new call after the window elapses");

        // --- Path guard ---
        string fullPath, error;
        bool ok1 = MCPPathGuard.TryResolveWithinAssets("/fake/project", "Prefabs/Enemy.prefab", out fullPath, out error);
        Check(ok1 && error == null, "path guard allows a normal relative path under Assets/");

        bool ok2 = MCPPathGuard.TryResolveWithinAssets("/fake/project", "../../etc/passwd", out fullPath, out error);
        Check(!ok2 && error != null, "path guard blocks '..' traversal outside Assets/: " + error);

        bool ok3 = MCPPathGuard.TryResolveWithinAssets("/fake/project", "/etc/passwd", out fullPath, out error);
        Check(!ok3 && error != null, "path guard blocks an absolute path: " + error);

        // Must run before anything below ever touches the MCPServer type: doing so
        // triggers its static constructor, which calls the real Start() and leaves
        // MCPInstanceLock (a process-wide singleton, correctly so in production --
        // one process should only ever hold the lock for one project) held for
        // whatever MCPProjectUtil.ProjectRoot resolves to at that moment. Running
        // this test AFTER that point would mean its own TryAcquire/Release calls
        // against unrelated temp paths would collide with that already-held real
        // instance instead of exercising the primitive in isolation.
        TestInstanceLock();

        TestScriptingTools(ctx);
        TestPhysicsTools(ctx);
        TestAssetTools(ctx);
        TestUITools(ctx);
        TestPhase4Performance(ctx);

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("All Phase 2 + Phase 3 (Scripting + Physics + Assets + UI) + Phase 4 (Performance) C# logic checks passed.");
            return 0;
        }
        else
        {
            Console.WriteLine(_failures + " check(s) FAILED.");
            return 1;
        }
    }

    private static void TestScriptingTools(MCPToolContext ctx)
    {
        // Point Application.dataPath at a real temp directory so ScriptingTools'
        // actual File/Directory calls run against real disk I/O, not a mock.
        var tempRoot = Path.Combine(Path.GetTempPath(), "mcp_scripting_test_" + Guid.NewGuid().ToString("N"));
        var assetsDir = Path.Combine(tempRoot, "Assets");
        Directory.CreateDirectory(assetsDir);
        UnityEngine.Application.dataPath = assetsDir;

        try
        {
            // 1. create_script (MonoBehaviour default)
            var create = MCPToolRegistry.Invoke(
                "create_script", new Dictionary<string, object> { ["path"] = "Scripts/TestBehaviour.cs" }, ctx);
            Check(create.Ok, "create_script succeeds for a new path: " + create.Error);

            var diskPath = Path.Combine(assetsDir, "Scripts", "TestBehaviour.cs");
            Check(File.Exists(diskPath), "create_script actually wrote a file to disk at the expected path");

            var writtenContent = File.Exists(diskPath) ? File.ReadAllText(diskPath) : "";
            Check(writtenContent.Contains("class TestBehaviour") && writtenContent.Contains(": MonoBehaviour"),
                "created file contains the expected class declaration");

            // 2. create_script again on the same path should fail (already exists)
            var createAgain = MCPToolRegistry.Invoke(
                "create_script", new Dictionary<string, object> { ["path"] = "Scripts/TestBehaviour.cs" }, ctx);
            Check(!createAgain.Ok, "create_script on an existing path is rejected: " + createAgain.Error);

            // 3. read_script
            var read = MCPToolRegistry.Invoke(
                "read_script", new Dictionary<string, object> { ["path"] = "Scripts/TestBehaviour.cs" }, ctx);
            Check(read.Ok, "read_script succeeds: " + read.Error);

            // 4. update_script
            var newContent = "using UnityEngine;\npublic class TestBehaviour : MonoBehaviour { public int marker = 42; }\n";
            var update = MCPToolRegistry.Invoke(
                "update_script",
                new Dictionary<string, object> { ["path"] = "Scripts/TestBehaviour.cs", ["content"] = newContent },
                ctx);
            Check(update.Ok, "update_script succeeds on an existing file: " + update.Error);
            Check(File.ReadAllText(diskPath) == newContent, "update_script actually overwrote the file content on disk");

            // update_script on a nonexistent file should fail
            var updateMissing = MCPToolRegistry.Invoke(
                "update_script",
                new Dictionary<string, object> { ["path"] = "Scripts/DoesNotExist.cs", ["content"] = "x" },
                ctx);
            Check(!updateMissing.Ok, "update_script on a nonexistent file is rejected: " + updateMissing.Error);

            // 5. list_scripts
            var list = MCPToolRegistry.Invoke("list_scripts", new Dictionary<string, object>(), ctx);
            Check(list.Ok, "list_scripts succeeds: " + list.Error);

            // 6. delete_script requires confirm
            var deleteNoConfirm = MCPToolRegistry.Invoke(
                "delete_script", new Dictionary<string, object> { ["path"] = "Scripts/TestBehaviour.cs" }, ctx);
            Check(!deleteNoConfirm.Ok, "delete_script without confirm is rejected: " + deleteNoConfirm.Error);
            Check(File.Exists(diskPath), "file still exists on disk after the rejected delete");

            var deleteConfirmed = MCPToolRegistry.Invoke(
                "delete_script",
                new Dictionary<string, object> { ["path"] = "Scripts/TestBehaviour.cs", ["confirm"] = true },
                ctx);
            Check(deleteConfirmed.Ok, "delete_script with confirm=true succeeds: " + deleteConfirmed.Error);
            Check(!File.Exists(diskPath), "file was actually removed from disk after confirmed delete");

            // 7. get_compile_status runs without error and returns the expected shape
            var status = MCPToolRegistry.Invoke("get_compile_status", new Dictionary<string, object>(), ctx);
            Check(status.Ok, "get_compile_status succeeds: " + status.Error);

            // 8. Guardrails: non-.cs extension rejected
            var badExt = MCPToolRegistry.Invoke(
                "create_script", new Dictionary<string, object> { ["path"] = "Scripts/NotAScript.txt" }, ctx);
            Check(!badExt.Ok && badExt.Error.Contains(".cs"), "create_script rejects a non-.cs path: " + badExt.Error);

            // 9. Guardrails: path traversal outside Assets/ rejected
            var traversal = MCPToolRegistry.Invoke(
                "create_script", new Dictionary<string, object> { ["path"] = "../../outside.cs" }, ctx);
            Check(!traversal.Ok, "create_script rejects a path that escapes Assets/: " + traversal.Error);

            // 10. Guardrails: invalid class identifier derived from filename
            var badName = MCPToolRegistry.Invoke(
                "create_script", new Dictionary<string, object> { ["path"] = "Scripts/123Bad.cs" }, ctx);
            Check(!badName.Ok, "create_script rejects a filename that isn't a valid C# identifier: " + badName.Error);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static void TestPhysicsTools(MCPToolContext ctx)
    {
        // Direct unit test of the override-only-supplied-axes helper -- this is the
        // actual novel logic in the Physics module. It's tested directly rather than
        // through MCPToolRegistry.Invoke because every physics tool resolves its
        // GameObject via MCPSceneUtil.ResolvePath first, which always returns null
        // against this stub's empty scene (the same limitation the existing
        // SceneTools/ComponentTools tests have -- nothing new here).
        var vecX = new UnityEngine.Vector3(1f, 2f, 3f);
        PhysicsTools.ApplyVector3Overrides(ref vecX, 10f, null, null);
        Check(vecX.x == 10f && vecX.y == 2f && vecX.z == 3f,
            "Vector3 override applies only the supplied x axis, leaves y/z unchanged");

        var vecNone = new UnityEngine.Vector3(1f, 2f, 3f);
        PhysicsTools.ApplyVector3Overrides(ref vecNone, null, null, null);
        Check(vecNone.x == 1f && vecNone.y == 2f && vecNone.z == 3f,
            "Vector3 override with all axes null leaves every axis unchanged");

        var vecAll = new UnityEngine.Vector3(0f, 0f, 0f);
        PhysicsTools.ApplyVector3Overrides(ref vecAll, 5f, 6f, 7f);
        Check(vecAll.x == 5f && vecAll.y == 6f && vecAll.z == 7f,
            "Vector3 override with all axes supplied sets all three");

        // Registry-level routing/guard-clause checks. These only reach the "path not
        // found" branch in this stub (see note above) -- that's still a real,
        // meaningful check that the tool is correctly wired into the registry with
        // the right parameter names and enum coercion.
        var addColliderMissing = MCPToolRegistry.Invoke(
            "add_collider", new Dictionary<string, object> { ["path"] = "Nope", ["type"] = "Box" }, ctx);
        Check(!addColliderMissing.Ok && addColliderMissing.Error != null && addColliderMissing.Error.Contains("not found"),
            "add_collider on a nonexistent path fails cleanly (enum arg 'Box' coerced correctly): " + addColliderMissing.Error);

        var configureRbMissing = MCPToolRegistry.Invoke(
            "configure_rigidbody", new Dictionary<string, object> { ["path"] = "Nope" }, ctx);
        Check(!configureRbMissing.Ok && configureRbMissing.Error != null && configureRbMissing.Error.Contains("not found"),
            "configure_rigidbody on a nonexistent path fails cleanly: " + configureRbMissing.Error);

        var setVelocityMissing = MCPToolRegistry.Invoke(
            "set_velocity", new Dictionary<string, object> { ["path"] = "Nope", ["velX"] = 1f }, ctx);
        Check(!setVelocityMissing.Ok && setVelocityMissing.Error != null && setVelocityMissing.Error.Contains("not found"),
            "set_velocity on a nonexistent path fails cleanly: " + setVelocityMissing.Error);

        // raycast's own validation error is reachable WITHOUT any GameObject
        // resolution -- genuinely exercises that code path, not just the stub's limits.
        var raycastNoOrigin = MCPToolRegistry.Invoke("raycast", new Dictionary<string, object>(), ctx);
        Check(!raycastNoOrigin.Ok && raycastNoOrigin.Error != null && raycastNoOrigin.Error.Contains("Provide either fromPath"),
            "raycast with neither fromPath nor explicit origin is rejected: " + raycastNoOrigin.Error);

        // raycast with an explicit origin bypasses GameObject resolution entirely and
        // reaches the real Physics.Raycast call -- genuinely end-to-end in this stub.
        var raycastExplicit = MCPToolRegistry.Invoke(
            "raycast",
            new Dictionary<string, object> { ["originX"] = 0f, ["originY"] = 5f, ["originZ"] = 0f },
            ctx);
        Check(raycastExplicit.Ok, "raycast with an explicit origin succeeds end-to-end through Physics.Raycast: " + raycastExplicit.Error);
    }

    private static void TestAssetTools(MCPToolContext ctx)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mcp_asset_test_" + Guid.NewGuid().ToString("N"));
        var assetsDir = Path.Combine(tempRoot, "Assets");
        Directory.CreateDirectory(assetsDir);
        UnityEngine.Application.dataPath = assetsDir;

        try
        {
            // create_material: no GameObject resolution involved at all -- genuinely
            // end-to-end in this stub, though AssetDatabase.CreateAsset itself is a
            // no-op stub, so the returned payload is what's being verified, not a file
            // on disk (see set_material_color below for the file-verified case).
            var createMat = MCPToolRegistry.Invoke(
                "create_material", new Dictionary<string, object> { ["assetPath"] = "Materials/Test.mat" }, ctx);
            Check(createMat.Ok, "create_material succeeds with defaults: " + createMat.Error);

            var badExt = MCPToolRegistry.Invoke(
                "create_material", new Dictionary<string, object> { ["assetPath"] = "Materials/Test.txt" }, ctx);
            Check(!badExt.Ok && badExt.Error.Contains(".mat"), "create_material rejects a non-.mat path: " + badExt.Error);

            // create_scriptable_object: genuinely end-to-end using a real test class
            // that derives from UnityEngine.ScriptableObject, discovered via the same
            // MCPTypeResolver reflection scan used in production.
            var createSO = MCPToolRegistry.Invoke(
                "create_scriptable_object",
                new Dictionary<string, object> { ["typeName"] = "TestScriptableObjectClass", ["assetPath"] = "Data/Test.asset" },
                ctx);
            Check(createSO.Ok, "create_scriptable_object succeeds for a real ScriptableObject subclass: " + createSO.Error);

            var createSOWrongBase = MCPToolRegistry.Invoke(
                "create_scriptable_object",
                new Dictionary<string, object> { ["typeName"] = "TestTool", ["assetPath"] = "Data/Test2.asset" },
                ctx);
            Check(!createSOWrongBase.Ok, "create_scriptable_object rejects a type that isn't a ScriptableObject: " + createSOWrongBase.Error);

            // Write a real dummy asset file directly (bypassing create_material's
            // no-op AssetDatabase.CreateAsset stub) so list_assets and delete_asset
            // have something real on disk to find and remove.
            var dummyMatPath = Path.Combine(assetsDir, "Materials", "Dummy.mat");
            Directory.CreateDirectory(Path.GetDirectoryName(dummyMatPath));
            File.WriteAllText(dummyMatPath, "%YAML dummy material for test purposes");

            var list = MCPToolRegistry.Invoke(
                "list_assets", new Dictionary<string, object> { ["extension"] = "mat" }, ctx);
            Check(list.Ok, "list_assets succeeds: " + list.Error);

            var deleteNoConfirm = MCPToolRegistry.Invoke(
                "delete_asset", new Dictionary<string, object> { ["assetPath"] = "Materials/Dummy.mat" }, ctx);
            Check(!deleteNoConfirm.Ok, "delete_asset without confirm is rejected: " + deleteNoConfirm.Error);
            Check(File.Exists(dummyMatPath), "dummy asset file still exists after the rejected delete");

            var deleteConfirmed = MCPToolRegistry.Invoke(
                "delete_asset",
                new Dictionary<string, object> { ["assetPath"] = "Materials/Dummy.mat", ["confirm"] = true },
                ctx);
            Check(deleteConfirmed.Ok, "delete_asset with confirm=true succeeds: " + deleteConfirmed.Error);
            Check(!File.Exists(dummyMatPath), "dummy asset file was actually removed from disk");

            // instantiate_prefab: reaches AssetDatabase.LoadAssetAtPath<GameObject>
            // (stub always returns null, mirroring "not actually a GameObject asset")
            // once a real file exists on disk at the guarded path.
            var dummyPrefabPath = Path.Combine(assetsDir, "Prefab.prefab");
            File.WriteAllText(dummyPrefabPath, "%YAML dummy prefab for test purposes");
            var instantiate = MCPToolRegistry.Invoke(
                "instantiate_prefab", new Dictionary<string, object> { ["assetPath"] = "Prefab.prefab" }, ctx);
            Check(!instantiate.Ok && instantiate.Error.Contains("Could not load"),
                "instantiate_prefab fails cleanly when the asset can't be loaded as a GameObject (stub limitation, not a real prefab): "
                + instantiate.Error);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static void TestUITools(MCPToolContext ctx)
    {
        // Direct unit test of the Vector2 override helper -- the UI-module analog of
        // PhysicsTools' Vector3 override helper, same rationale for testing directly.
        var vec = new UnityEngine.Vector2(1f, 2f);
        UITools.ApplyVector2Overrides(ref vec, 10f, null);
        Check(vec.x == 10f && vec.y == 2f, "Vector2 override applies only the supplied x axis, leaves y unchanged");

        var vecNone = new UnityEngine.Vector2(1f, 2f);
        UITools.ApplyVector2Overrides(ref vecNone, null, null);
        Check(vecNone.x == 1f && vecNone.y == 2f, "Vector2 override with both axes null leaves both unchanged");

        // create_canvas needs no parent/GameObject resolution at all -- genuinely
        // end-to-end now that the stub's GameObject auto-initializes a Transform
        // (matching real Unity, where every GameObject always has one).
        var createCanvas = MCPToolRegistry.Invoke(
            "create_canvas", new Dictionary<string, object> { ["name"] = "TestCanvas" }, ctx);
        Check(createCanvas.Ok, "create_canvas succeeds end-to-end: " + createCanvas.Error);

        var createCanvasWorldSpace = MCPToolRegistry.Invoke(
            "create_canvas",
            new Dictionary<string, object> { ["name"] = "WorldCanvas", ["renderMode"] = "WorldSpace" },
            ctx);
        Check(createCanvasWorldSpace.Ok, "create_canvas accepts an explicit renderMode enum value: " + createCanvasWorldSpace.Error);

        // create_ui_element / set_rect_transform / set_layout / set_ui_color all
        // resolve a GameObject by path first, which -- same limitation noted
        // throughout -- always fails against this stub's empty scene. Still a real,
        // meaningful check that routing and required-argument handling are correct.
        var createElementMissing = MCPToolRegistry.Invoke(
            "create_ui_element",
            new Dictionary<string, object> { ["type"] = "Button", ["parentPath"] = "Nope" },
            ctx);
        Check(!createElementMissing.Ok && createElementMissing.Error.Contains("not found"),
            "create_ui_element on a nonexistent parent path fails cleanly (enum arg 'Button' coerced correctly): "
            + createElementMissing.Error);

        var setRectMissing = MCPToolRegistry.Invoke(
            "set_rect_transform", new Dictionary<string, object> { ["path"] = "Nope", ["posX"] = 5f }, ctx);
        Check(!setRectMissing.Ok && setRectMissing.Error.Contains("not found"),
            "set_rect_transform on a nonexistent path fails cleanly: " + setRectMissing.Error);

        var setLayoutMissing = MCPToolRegistry.Invoke(
            "set_layout", new Dictionary<string, object> { ["path"] = "Nope", ["type"] = "Vertical" }, ctx);
        Check(!setLayoutMissing.Ok && setLayoutMissing.Error.Contains("not found"),
            "set_layout on a nonexistent path fails cleanly (enum arg 'Vertical' coerced correctly): " + setLayoutMissing.Error);

        var setColorMissing = MCPToolRegistry.Invoke(
            "set_ui_color", new Dictionary<string, object> { ["path"] = "Nope", ["r"] = 1f }, ctx);
        Check(!setColorMissing.Ok && setColorMissing.Error.Contains("not found"),
            "set_ui_color on a nonexistent path fails cleanly: " + setColorMissing.Error);
    }

    private static void TestPhase4Performance(MCPToolContext ctx)
    {
        // --- Hierarchy cache: build-once-then-cached, invalidated on hierarchyChanged ---
        int buildCount = 0;
        Func<object> builder = () =>
        {
            buildCount++;
            return new { call = buildCount };
        };

        var first = MCPHierarchyCache.GetOrBuild(builder);
        var second = MCPHierarchyCache.GetOrBuild(builder);
        Check(buildCount == 1, "hierarchy cache builds once across two GetOrBuild calls with no invalidation in between");
        Check(ReferenceEquals(first, second), "hierarchy cache returns the exact same cached instance on a cache hit");

        UnityEditor.EditorApplication.RaiseHierarchyChangedForTest();
        var third = MCPHierarchyCache.GetOrBuild(builder);
        Check(buildCount == 2, "hierarchy cache rebuilds after hierarchyChanged fires");
        Check(!ReferenceEquals(first, third), "post-invalidation build returns a new instance, not the stale cached one");

        // --- Rate limiter: N-token batch charging ---
        var limiter = new MCPRateLimiter(maxPerWindow: 10, window: TimeSpan.FromMilliseconds(500));
        Check(limiter.TryAcquire(4), "rate limiter allows a charge of 4 against a budget of 10");
        Check(limiter.TryAcquire(4), "rate limiter allows a second charge of 4 (8 total, still under 10)");
        Check(!limiter.TryAcquire(4), "rate limiter blocks a third charge of 4 (would be 12, over the 10 budget)");
        Check(limiter.TryAcquire(2), "rate limiter allows a charge of 2 that exactly fills the remaining budget (10 total)");
        Check(!limiter.TryAcquire(1), "rate limiter blocks even a single-token charge once the budget is fully spent");

        // --- batch_execute: real end-to-end test through MCPServer.Enqueue + MCPCommandDispatcher.DrainQueue,
        // not a direct call to the private HandleBatch -- this exercises the actual enqueue/routing/dispatch
        // pipeline a real client's batch message goes through.
        MCPMessage capturedBatchResponse = null;
        var batchMsg = new MCPMessage
        {
            type = "batch",
            id = "batch-1",
            calls = new List<MCPBatchCallItem>
            {
                new MCPBatchCallItem { tool = "test_nondestructive", args = new Dictionary<string, object> { ["value"] = "a" } },
                new MCPBatchCallItem { tool = "test_slow" },
                new MCPBatchCallItem { tool = "test_nondestructive", args = new Dictionary<string, object> { ["value"] = "b" } },
            }
        };
        MCPServer.Enqueue(new MCPServer.PendingCommand { Message = batchMsg, Respond = r => capturedBatchResponse = r });
        MCPCommandDispatcher.DrainQueue();

        Check(capturedBatchResponse != null && capturedBatchResponse.type == "batch_result", "batch produces a batch_result response");
        Check(capturedBatchResponse?.results?.Count == 3, "batch_result contains one entry per sub-call, in order");
        Check(capturedBatchResponse?.results[0].ok == true, "batch item 0 (fast tool) succeeds");
        Check(capturedBatchResponse?.results[1].ok == false && capturedBatchResponse.results[1].error.Contains("cannot run inside a batch"),
            "batch item 1 (Slow-tier tool) is rejected with a clear message, not silently dropped or run: " + capturedBatchResponse?.results[1].error);
        Check(capturedBatchResponse?.results[2].ok == true, "batch item 2 (fast tool, after the rejected slow one) still runs normally");

        // Empty batch and oversized batch are rejected at the batch level (not per-item).
        MCPMessage capturedEmptyResponse = null;
        MCPServer.Enqueue(new MCPServer.PendingCommand
        {
            Message = new MCPMessage { type = "batch", id = "batch-empty", calls = new List<MCPBatchCallItem>() },
            Respond = r => capturedEmptyResponse = r
        });
        MCPCommandDispatcher.DrainQueue();
        Check(capturedEmptyResponse != null && !capturedEmptyResponse.ok && capturedEmptyResponse.error.Contains("no calls"),
            "an empty batch is rejected at the batch level: " + capturedEmptyResponse?.error);

        var oversizedCalls = new List<MCPBatchCallItem>();
        for (int i = 0; i < 26; i++) oversizedCalls.Add(new MCPBatchCallItem { tool = "test_nondestructive", args = new Dictionary<string, object> { ["value"] = i.ToString() } });
        MCPMessage capturedOversizedResponse = null;
        MCPServer.Enqueue(new MCPServer.PendingCommand
        {
            Message = new MCPMessage { type = "batch", id = "batch-oversized", calls = oversizedCalls },
            Respond = r => capturedOversizedResponse = r
        });
        MCPCommandDispatcher.DrainQueue();
        Check(capturedOversizedResponse != null && !capturedOversizedResponse.ok && capturedOversizedResponse.error.Contains("exceeds the max"),
            "a batch of 26 (over the 25 cap) is rejected at the batch level: " + capturedOversizedResponse?.error);

        // --- Priority queue: fast calls enqueued AFTER a slow one still get processed first ---
        var processedOrder = new List<string>();
        MCPServer.Enqueue(new MCPServer.PendingCommand
        {
            Message = new MCPMessage { type = "call", id = "order-slow", tool = "test_slow" },
            Respond = r => processedOrder.Add("slow")
        });
        MCPServer.Enqueue(new MCPServer.PendingCommand
        {
            Message = new MCPMessage { type = "call", id = "order-fast-1", tool = "test_nondestructive", args = new Dictionary<string, object> { ["value"] = "x" } },
            Respond = r => processedOrder.Add("fast1")
        });
        MCPServer.Enqueue(new MCPServer.PendingCommand
        {
            Message = new MCPMessage { type = "call", id = "order-fast-2", tool = "test_nondestructive", args = new Dictionary<string, object> { ["value"] = "y" } },
            Respond = r => processedOrder.Add("fast2")
        });
        MCPCommandDispatcher.DrainQueue();

        Check(processedOrder.Count == 3, "all three queued items were processed in this DrainQueue tick");
        Check(processedOrder.IndexOf("fast1") < processedOrder.IndexOf("slow") && processedOrder.IndexOf("fast2") < processedOrder.IndexOf("slow"),
            "both fast calls are processed before the slow call despite being enqueued after it: [" + string.Join(", ", processedOrder) + "]");

        TestToolGroups();
        TestParameterSchemaPolish();
        TestArrayParameterSupport();
        TestSetupWindowLogic();
        TestInstanceConflictDetection();
        TestAuthHandshakeTokenSecurity();
        TestNoDuplicatePlayModeTools();
        TestClientCountRestartContext();
        TestPathGuardSymlinkDetection();
        TestTypeResolverCachingAndAmbiguity();
        TestHierarchyCacheObjectChangeEvents();
    }

    private static void TestInstanceLock()
    {
        // Regression test for the actual reported incident: two live Unity processes
        // for the same project used to both keep overwriting session.json on every
        // domain reload (MCPInstanceConflictDetector only ever logged a warning, it
        // never stopped the second one from writing) -- a connected MCP client would
        // silently get redirected to whichever process wrote last, with no error at
        // all. MCPInstanceLock is the actual mutual-exclusion fix: it's an OS-level
        // exclusive file lock, so this test verifies the real primitive, not a mock of it.
        var tempRoot = Path.Combine(Path.GetTempPath(), "mcp_instancelock_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            bool first = MCPInstanceLock.TryAcquire(tempRoot, out var firstError);
            Check(first && firstError == null, "first TryAcquire for a project with no existing lock holder succeeds: " + firstError);
            Check(MCPInstanceLock.IsHeld, "IsHeld reflects that this process now holds the lock");

            bool second = MCPInstanceLock.TryAcquire(tempRoot, out var secondError);
            Check(second, "a second TryAcquire from the SAME process (already holding it) is idempotent, not a failure: " + secondError);

            MCPInstanceLock.Release();
            Check(!MCPInstanceLock.IsHeld, "IsHeld reflects that the lock was released");

            bool third = MCPInstanceLock.TryAcquire(tempRoot, out var thirdError);
            Check(third && thirdError == null, "TryAcquire succeeds again after Release() -- the same process can reacquire after letting go (models a domain reload's Stop() -> Start() cycle): " + thirdError);

            MCPInstanceLock.Release();

            // Simulate a genuinely different, still-live process holding the lock: an
            // independent FileStream opened with FileShare.None on the same lock file,
            // exactly what a second live Unity OS process would produce.
            var lockPath = Path.Combine(tempRoot, "Library", "MCP", "bridge.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath));
            using (var externalHolder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                bool blocked = MCPInstanceLock.TryAcquire(tempRoot, out var blockedError);
                Check(!blocked && !string.IsNullOrEmpty(blockedError),
                    "TryAcquire fails with a clear error when another handle already holds the lock file exclusively: " + blockedError);
                Check(!MCPInstanceLock.IsHeld, "IsHeld is false after a failed TryAcquire -- no false claim of ownership");
            }

            // Once the external holder releases (Dispose, above), the lock is free again.
            bool afterExternalRelease = MCPInstanceLock.TryAcquire(tempRoot, out var afterError);
            Check(afterExternalRelease && afterError == null,
                "TryAcquire succeeds once the previously-blocking external handle is closed: " + afterError);
            MCPInstanceLock.Release();
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static void TestAuthHandshakeTokenSecurity()
    {
        var tokenA = MCPAuthHandshake.EnsureToken();
        Check(!string.IsNullOrEmpty(tokenA) && tokenA.Length == 64,
            "EnsureToken produces a 64-hex-char (32-byte) token: length " + tokenA.Length);

        bool isHex = true;
        foreach (var c in tokenA)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) { isHex = false; break; }
        }
        Check(isHex, "the token is lowercase-hex-encoded: " + tokenA);

        Check(MCPAuthHandshake.Validate(tokenA), "Validate accepts the token that was just generated");
        Check(!MCPAuthHandshake.Validate(tokenA.Substring(0, 63) + (tokenA[63] == 'a' ? 'b' : 'a')),
            "Validate rejects a token that differs in only the last character");
        Check(!MCPAuthHandshake.Validate(null), "Validate rejects a null token without throwing");
        Check(!MCPAuthHandshake.Validate(""), "Validate rejects an empty token");

        var tokenB = MCPAuthHandshake.EnsureToken();
        Check(tokenA != tokenB, "two successive EnsureToken() calls produce different tokens");
        Check(!MCPAuthHandshake.Validate(tokenA), "the OLD token is rejected once a new one has been generated (EnsureToken fully replaces it, not appends)");
        Check(MCPAuthHandshake.Validate(tokenB), "the NEW token validates correctly");
    }

    private static void TestClientCountRestartContext()
    {
        // Regression test for a real reported confusion: the Setup window showed "0
        // clients" immediately upon entering Play Mode even though the AI agent's MCP
        // session never saw an interruption, with no indication of why. The 0 itself
        // was correct (Play Mode's domain reload -- on by default unless Enter Play
        // Mode Options disables it -- wipes MCPServer's static state, including
        // _connectedClientCount, and drops the bridge's live socket), but the Setup
        // window had no way to tell that apart from "nobody has ever connected" or
        // "something is actually broken". HadClientBeforeLastRestart/SecondsSinceStart
        // are what it now uses to tell those apart -- exercised here via a real
        // Stop()/Start() cycle (which is exactly what beforeAssemblyReload -> domain
        // reload -> the static constructor's Start() call looks like from a single
        // process's point of view), not a mock of it.
        bool wasRunning = MCPServer.BoundPort > 0;

        MCPServer.Stop();
        Check(double.IsPositiveInfinity(MCPServer.SecondsSinceStart), "SecondsSinceStart is +Infinity while the bridge is not running -- never a false 'just started' reading");
        Check(!MCPServer.HadClientBeforeLastRestart, "HadClientBeforeLastRestart is false after a Stop() with zero clients connected at the time");

        MCPServer.Start();
        Check(MCPServer.BoundPort > 0, "MCPServer restarts successfully after Stop() -- models the Stop() -> (domain reload) -> Start() cycle a Play Mode transition triggers by default");
        Check(!double.IsPositiveInfinity(MCPServer.SecondsSinceStart) && MCPServer.SecondsSinceStart >= 0,
            "SecondsSinceStart is a finite, non-negative reading once running again: " + MCPServer.SecondsSinceStart);

        // SessionState itself (not MCPServer) is what has to survive the domain
        // reload boundary -- sanity-check the underlying primitive directly, since a
        // plain static field would silently defeat the whole point of this fix.
        UnityEditor.SessionState.SetBool("mcp_test_session_key", true);
        Check(UnityEditor.SessionState.GetBool("mcp_test_session_key", false), "SessionState round-trips a bool set earlier in the same process (models surviving a domain reload, which this stub process never actually performs)");
        Check(UnityEditor.SessionState.GetBool("mcp_test_key_never_set", false) == false, "SessionState.GetBool returns the given default for a key that was never set");

        if (!wasRunning) MCPServer.Stop(); // leave state as found for anything that runs after this
    }

    private static void TestNoDuplicatePlayModeTools()
    {
        // Regression test for the second reported drawback: enter_play_mode,
        // exit_play_mode, and pause_play_mode used to be defined in BOTH
        // EditorStateTools.cs and PlayModeTools.cs. MCPToolRegistry.Rescan() silently
        // dropped whichever one it scanned second (assembly/type enumeration order is
        // not a stable contract), so which behavior was actually live could vary --
        // including the naive EditorStateTools version that returned immediately
        // instead of waiting for the Play Mode transition (and any domain reload it
        // triggers) to settle. Only PlayModeTools' versions should exist now.
        MCPToolRegistry.EnsureScanned();

        foreach (var name in new[] { "enter_play_mode", "exit_play_mode", "pause_play_mode" })
        {
            Check(MCPToolRegistry.TryGet(name, out var entry), $"'{name}' is registered exactly once (no silent duplicate-drop possible with only one definition left)");
            Check(entry?.Group == "testing", $"'{name}' resolves to the PlayModeTools definition (group 'testing'), not the removed EditorStateTools one (group 'core'): got '{entry?.Group}'");
        }

        MCPToolRegistry.TryGet("enter_play_mode", out var enterEntry);
        var enterProps = (Dictionary<string, object>)enterEntry.Schema["properties"];
        Check(enterProps.ContainsKey("timeoutSeconds"),
            "the surviving enter_play_mode is PlayModeTools' wait-for-settle version, identifiable by its 'timeoutSeconds' parameter (the removed EditorStateTools version had none)");

        Check(MCPToolRegistry.TryGet("save_project", out var saveEntry) && saveEntry.Group == "core",
            "save_project (never duplicated) is still registered under 'core', confirming EditorStateTools itself wasn't broken by removing the duplicates from it");
    }

    private static void TestParameterSchemaPolish()
    {
        MCPToolRegistry.EnsureScanned();

        // --- [MCPParam] descriptions actually make it into the generated schema ---
        MCPToolRegistry.TryGet("create_gameobject", out var createGoEntry);
        var createGoProps = (Dictionary<string, object>)createGoEntry.Schema["properties"];
        var nameProp = (Dictionary<string, object>)createGoProps["name"];
        Check(nameProp.ContainsKey("description") && ((string)nameProp["description"]).Length > 0,
            "an [MCPParam]-annotated parameter ('name' on create_gameobject) has a non-empty description in its schema: "
            + nameProp.GetValueOrDefault("description"));

        // A parameter with NO [MCPParam] still gets a bare schema entry (no crash, no
        // stray "description" key) -- this stays fully optional, not a breaking change.
        MCPToolRegistry.TryGet("test_nondestructive", out var testToolEntry);
        var testToolProps = (Dictionary<string, object>)testToolEntry.Schema["properties"];
        var valueProp = (Dictionary<string, object>)testToolProps["value"];
        Check(!valueProp.ContainsKey("description"),
            "a parameter with no [MCPParam] gets no 'description' key at all (purely additive, not required)");

        // --- Enum-typed parameters get an explicit "enum" value list ---
        MCPToolRegistry.TryGet("add_collider", out var addColliderEntry);
        var addColliderProps = (Dictionary<string, object>)addColliderEntry.Schema["properties"];
        var typeProp = (Dictionary<string, object>)addColliderProps["type"];
        Check(typeProp.ContainsKey("enum"), "add_collider's enum-typed 'type' parameter has an 'enum' key in its schema");

        var enumValues = (string[])typeProp["enum"];
        Check(enumValues != null && new HashSet<string>(enumValues).SetEquals(new[] { "Box", "Sphere", "Capsule" }),
            "add_collider's 'type' enum lists exactly Box/Sphere/Capsule: " + string.Join(",", enumValues ?? new string[0]));

        // A non-enum parameter (string) must NOT get a stray "enum" key.
        var pathPropOnCollider = (Dictionary<string, object>)addColliderProps["path"];
        Check(!pathPropOnCollider.ContainsKey("enum"), "a plain string parameter ('path') has no spurious 'enum' key");

        // Spot-check a second enum across a different tool/module to make sure this
        // isn't a one-off that happens to work for add_collider specifically.
        MCPToolRegistry.TryGet("apply_force", out var applyForceEntry);
        var applyForceProps = (Dictionary<string, object>)applyForceEntry.Schema["properties"];
        var modeProp = (Dictionary<string, object>)applyForceProps["mode"];
        var modeEnumValues = (string[])modeProp["enum"];
        Check(modeEnumValues != null && new HashSet<string>(modeEnumValues).SetEquals(
                new[] { "Force", "Impulse", "VelocityChange", "Acceleration" }),
            "apply_force's 'mode' enum lists all four ForceMode values: " + string.Join(",", modeEnumValues ?? new string[0]));

        // --- The guide's own §1 example, verified end-to-end, not just eyeballed ---
        MCPToolRegistry.TryGet("spawn_coin", out var spawnCoinEntry);
        Check(spawnCoinEntry != null, "docs/writing-custom-tools.md's example tool ('spawn_coin') is discovered by the registry with zero extra registration");

        var spawnCoinProps = (Dictionary<string, object>)spawnCoinEntry.Schema["properties"];
        var xProp = (Dictionary<string, object>)spawnCoinProps["x"];
        Check((string)xProp["description"] == "World-space X position.",
            "the guide example's [MCPParam] description on 'x' made it into the schema exactly as written");

        var ctx2 = new MCPToolContext { RequestId = "guide-example-test" };
        var spawnResult = MCPToolRegistry.Invoke(
            "spawn_coin", new Dictionary<string, object> { ["x"] = 1f, ["y"] = 2f, ["z"] = 3f }, ctx2);
        Check(spawnResult.Ok, "the guide example's tool actually runs successfully end-to-end (no GameObject resolution needed, so this isn't limited by the usual stub gap): " + spawnResult.Error);
    }

    private static void TestArrayParameterSupport()
    {
        // Regression test for a real gap found while building out the 300-tool
        // catalog: MCPToolRegistry had no support at all for string[]-typed
        // parameters -- BuildSchema fell through to a bare "string" type (wrong
        // shape entirely) and ConvertArg would throw trying to Convert.ChangeType a
        // JSON array into a string[]. A large fraction of the remaining catalog
        // (references, tags, bindings, inventories, ...) needs list-of-strings
        // parameters, so this had to be fixed before building those tools, not
        // worked around per-tool.
        MCPToolRegistry.EnsureScanned();

        MCPToolRegistry.TryGet("test_string_array", out var entry);
        Check(entry != null, "test_string_array is registered");

        var props = (Dictionary<string, object>)entry.Schema["properties"];
        var tagsProp = (Dictionary<string, object>)props["tags"];
        Check((string)tagsProp["type"] == "array", "a string[] parameter's schema type is 'array', not a bare 'string': " + tagsProp["type"]);
        var items = (Dictionary<string, object>)tagsProp["items"];
        Check((string)items["type"] == "string", "the array schema's 'items' declares string elements: " + items["type"]);

        var required = (List<string>)entry.Schema["required"];
        Check(required.Contains("tags"), "a required string[] parameter (no default) is marked required in the schema");
        Check(!required.Contains("extras"), "an optional string[] parameter (default null) is NOT marked required");

        var ctx = new MCPToolContext { RequestId = "array-test" };

        // The wire path: Newtonsoft deserializes a JSON array nested inside a
        // Dictionary<string, object> as a JArray, not a plain List<object> -- exercise
        // that exact type, not a stand-in, since that's what ConvertArg actually has
        // to handle in production.
        var wireArray = new Newtonsoft.Json.Linq.JArray("alpha", "beta", "gamma");
        var wireResult = MCPToolRegistry.Invoke(
            "test_string_array", new Dictionary<string, object> { ["tags"] = wireArray }, ctx);
        Check(wireResult.Ok, "a real JArray (the actual wire deserialization shape) is accepted for a string[] parameter: " + wireResult.Error);

        // Also accept a plain List<object>/object[] -- ConvertArg is written against
        // IEnumerable generally, not JArray specifically, so a direct (non-wire) caller
        // passing a real collection must work too.
        var listResult = MCPToolRegistry.Invoke(
            "test_string_array",
            new Dictionary<string, object> { ["tags"] = new List<object> { "x", "y" } },
            ctx);
        int listTagCount = listResult.Ok ? (int)listResult.Data.GetType().GetProperty("tagCount").GetValue(listResult.Data) : -1;
        Check(listResult.Ok && listTagCount == 2,
            "a plain List<object> is also accepted and coerced to the correct string[] length: " + listResult.Error);

        // Omitting the optional string[] entirely must not throw -- it should fall
        // through to its null default, same as any other optional parameter.
        var omittedResult = MCPToolRegistry.Invoke(
            "test_string_array",
            new Dictionary<string, object> { ["tags"] = new Newtonsoft.Json.Linq.JArray("solo") },
            ctx);
        Check(omittedResult.Ok, "omitting the optional string[] parameter entirely does not throw: " + omittedResult.Error);
    }

    private static void TestToolGroups()
    {
        MCPToolRegistry.EnsureScanned();

        // The 15 Phase-1 Scene/Component/Query tools declare no explicit group, so
        // they must default to "core" -- this is what lets 25 tools across four
        // modules be tagged without touching a single one of these 15.
        string[] coreTools =
        {
            "create_gameobject", "delete_gameobject", "duplicate_gameobject", "set_transform",
            "reparent_gameobject", "find_gameobjects",
            "add_component", "remove_component", "list_components", "get_component_field", "set_component_field",
            "get_hierarchy", "get_selected_object", "get_console_logs", "get_project_info",
        };
        bool allCore = true;
        foreach (var name in coreTools)
        {
            if (!MCPToolRegistry.TryGet(name, out var entry) || entry.Group != "core") allCore = false;
        }
        Check(allCore, $"all {coreTools.Length} Phase-1 Scene/Component/Query tools default to group 'core' with no attribute changes");

        var expectedGroups = new Dictionary<string, string>
        {
            ["create_script"] = "scripting", ["read_script"] = "scripting", ["update_script"] = "scripting",
            ["delete_script"] = "scripting", ["list_scripts"] = "scripting", ["get_compile_status"] = "scripting",
            ["add_collider"] = "physics", ["configure_rigidbody"] = "physics", ["set_velocity"] = "physics",
            ["apply_force"] = "physics", ["get_rigidbody_state"] = "physics", ["raycast"] = "physics",
            ["create_prefab"] = "assets", ["instantiate_prefab"] = "assets", ["create_material"] = "assets",
            ["set_material_color"] = "assets", ["create_scriptable_object"] = "assets", ["list_assets"] = "assets",
            ["delete_asset"] = "assets",
            ["create_canvas"] = "ui", ["create_ui_element"] = "ui", ["set_rect_transform"] = "ui",
            ["get_rect_transform"] = "ui", ["set_layout"] = "ui", ["set_ui_color"] = "ui",
        };

        bool allCorrect = true;
        var mismatches = new List<string>();
        foreach (var kv in expectedGroups)
        {
            if (!MCPToolRegistry.TryGet(kv.Key, out var entry) || entry.Group != kv.Value)
            {
                allCorrect = false;
                mismatches.Add($"{kv.Key} (expected '{kv.Value}', got '{(entry?.Group ?? "MISSING")}')");
            }
        }
        Check(allCorrect, $"all {expectedGroups.Count} module tools (scripting/physics/assets/ui) are tagged with the correct group"
            + (mismatches.Count > 0 ? ": " + string.Join(", ", mismatches) : ""));

        // Group also has to make it into the wire descriptor Python actually reads.
        MCPMessage capturedListToolsResponse = null;
        MCPServer.Enqueue(new MCPServer.PendingCommand
        {
            Message = new MCPMessage { type = "list_tools", id = "group-wire-check" },
            Respond = r => capturedListToolsResponse = r
        });
        MCPCommandDispatcher.DrainQueue();

        var scriptingDescriptor = capturedListToolsResponse?.tools?.Find(t => t.name == "create_script");
        Check(scriptingDescriptor != null && scriptingDescriptor.group == "scripting",
            "the 'group' field survives all the way into the list_tools wire descriptor: " + scriptingDescriptor?.group);
    }

    private static void TestSetupWindowLogic()
    {
        // --- ServerName sanitization ---
        Check(MCPClientDetector.ServerName("MyGame") == "unity-MyGame", "ServerName builds 'unity-<project>' for a clean folder name");
        Check(MCPClientDetector.ServerName("My Game!") == "unity-My-Game-", "ServerName replaces non-alphanumeric chars with '-': " + MCPClientDetector.ServerName("My Game!"));
        Check(MCPClientDetector.ServerName("") == "unity", "ServerName falls back to plain 'unity' for an empty project folder name");
        Check(MCPClientDetector.ServerName(null) == "unity", "ServerName falls back to plain 'unity' for a null project folder name");

        // --- AllKinds / DisplayName cover all four clients ---
        var allKinds = new List<MCPClientKind>(MCPClientDetector.AllKinds());
        Check(allKinds.Count == 4 && allKinds.Contains(MCPClientKind.ClaudeCode) && allKinds.Contains(MCPClientKind.Codex)
              && allKinds.Contains(MCPClientKind.Cursor) && allKinds.Contains(MCPClientKind.Antigravity),
            "AllKinds returns exactly the four supported clients: " + string.Join(", ", allKinds));
        Check(MCPClientDetector.DisplayName(MCPClientKind.Cursor) == "Cursor" && MCPClientDetector.DisplayName(MCPClientKind.Antigravity) == "Antigravity",
            "DisplayName has real names for the two newly-added clients");

        // --- MCPMcpConfigTargets: correct file location and format per client, verified
        // against current documentation for each rather than assumed ---
        Check(MCPMcpConfigTargets.RelativePathDisplay(MCPClientKind.ClaudeCode) == ".mcp.json", "Claude Code's project-scoped config is .mcp.json");
        Check(MCPMcpConfigTargets.RelativePathDisplay(MCPClientKind.Cursor) == ".cursor/mcp.json", "Cursor's project-scoped config is .cursor/mcp.json");
        Check(MCPMcpConfigTargets.RelativePathDisplay(MCPClientKind.Antigravity) == ".agents/mcp_config.json", "Antigravity's project-scoped config is .agents/mcp_config.json");
        Check(MCPMcpConfigTargets.RelativePathDisplay(MCPClientKind.Codex) == ".codex/config.toml", "Codex's project-scoped config is .codex/config.toml");
        Check(MCPMcpConfigTargets.Format(MCPClientKind.Codex) == MCPConfigFormat.Toml, "Codex is the only TOML-format client");
        Check(MCPMcpConfigTargets.Format(MCPClientKind.ClaudeCode) == MCPConfigFormat.Json
              && MCPMcpConfigTargets.Format(MCPClientKind.Cursor) == MCPConfigFormat.Json
              && MCPMcpConfigTargets.Format(MCPClientKind.Antigravity) == MCPConfigFormat.Json,
            "Claude Code, Cursor, and Antigravity all share the JSON format (same schema, confirmed identical across all three)");

        var absPath = MCPMcpConfigTargets.AbsolutePath(MCPClientKind.Cursor, "/Users/dev/MyGame");
        Check(absPath == "/Users/dev/MyGame/.cursor/mcp.json" || absPath == "/Users/dev/MyGame\\.cursor\\mcp.json",
            "AbsolutePath combines project root with the client's relative path correctly: " + absPath);

        // --- MCPServerEntryBuilder: absolute venv python path, explicit separators (not
        // Path.Combine, which uses the real runtime OS's separator regardless of the
        // logical isWindows flag -- the same class of bug found and fixed earlier for
        // MCPClientDetector.AbsoluteVenvPythonExecutable, applied consistently here too) ---
        Check(MCPServerEntryBuilder.AbsoluteVenvPythonExecutable("/Users/dev/python-server", isWindows: false) == "/Users/dev/python-server/.venv/bin/python3",
            "AbsoluteVenvPythonExecutable builds the correct Unix venv path");
        Check(MCPServerEntryBuilder.AbsoluteVenvPythonExecutable("C:\\Projects\\python-server", isWindows: true) == "C:\\Projects\\python-server\\.venv\\Scripts\\python.exe",
            "AbsoluteVenvPythonExecutable builds the correct Windows venv path, even when run on a non-Windows test machine");

        var builtArgs = MCPServerEntryBuilder.Args();
        Check(builtArgs.Count == 2 && builtArgs[0] == "-m" && builtArgs[1] == "unity_mcp_server.server", "Args() is exactly ['-m', 'unity_mcp_server.server']");

        var builtEnv = MCPServerEntryBuilder.Env("/Users/dev/MyGame", "/Users/dev/python-server");
        Check(builtEnv["UNITY_MCP_PROJECT_ROOT"] == "/Users/dev/MyGame" && builtEnv["PYTHONPATH"] == "/Users/dev/python-server",
            "Env() sets both UNITY_MCP_PROJECT_ROOT and PYTHONPATH explicitly -- this is what makes the generated config self-contained regardless of the client's own spawn-time working directory, closing the exact gap that caused the reported ModuleNotFoundError");

        TestMiniJsonParsing();
        TestMcpServersJsonWriterMerging();
        TestCodexTomlWriterMerging();
        TestEndToEndConfigFileWriting();
        TestPythonServerSettings();
        TestClientConfigTracker();
    }

    private static void TestPythonServerSettings()
    {
        // Moved here from the now-removed visual Tool Builder (MCPToolBuilderSettings)
        // -- the Setup window is the only remaining consumer of this setting. Confirms
        // the plain EditorPrefs get/set round-trip still works from its new home.
        MCPPythonServerSettings.PythonServerPath = "/Users/dev/python-server";
        Check(MCPPythonServerSettings.PythonServerPath == "/Users/dev/python-server",
            "PythonServerPath round-trips through EditorPrefs: " + MCPPythonServerSettings.PythonServerPath);

        MCPPythonServerSettings.PythonServerPath = "/somewhere/else";
        Check(MCPPythonServerSettings.PythonServerPath == "/somewhere/else",
            "PythonServerPath reflects an updated value, not just the first one ever set");
    }

    private static void TestClientConfigTracker()
    {
        // No client configured yet for this (test-harness-fake) project root --
        // TryGetLastConfigured must report false, not some stale/default value from
        // an unrelated earlier test (EditorPrefs in this stub is a flat dictionary
        // shared process-wide, same as real EditorPrefs is shared machine-wide for a
        // given key -- exercising this against a key that's genuinely never been
        // written is the real "nothing configured yet" case a fresh project sees).
        Check(!MCPClientConfigTracker.TryGetLastConfigured(out _, out _),
            "TryGetLastConfigured returns false when nothing has ever been recorded for this key");

        var now = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        MCPClientConfigTracker.RecordConfigured(MCPClientKind.Codex, now);

        bool found = MCPClientConfigTracker.TryGetLastConfigured(out var kind, out var configuredAt);
        Check(found, "TryGetLastConfigured returns true right after RecordConfigured");
        Check(kind == MCPClientKind.Codex, "the recorded client kind round-trips correctly: " + kind);
        Check(configuredAt == now, $"the recorded UTC timestamp round-trips exactly (no timezone drift through the 'o' round-trip format): expected {now:o}, got {configuredAt:o}");

        // Recording a second client overwrites the first -- this tracks the SINGLE
        // most recent Configure click, not a history of every client ever configured.
        var later = now.AddMinutes(5);
        MCPClientConfigTracker.RecordConfigured(MCPClientKind.Cursor, later);
        MCPClientConfigTracker.TryGetLastConfigured(out var kind2, out var configuredAt2);
        Check(kind2 == MCPClientKind.Cursor && configuredAt2 == later,
            "recording a second client replaces the first as 'last configured', it doesn't keep a history");

        // --- FormatRelativeTime: a pure function of two DateTimes, no EditorPrefs or real clock involved ---
        Check(MCPClientConfigTracker.FormatRelativeTime(now, now) == "just now", "zero elapsed time formats as 'just now'");
        Check(MCPClientConfigTracker.FormatRelativeTime(now, now.AddSeconds(30)) == "just now", "30 seconds elapsed still formats as 'just now'");
        Check(MCPClientConfigTracker.FormatRelativeTime(now, now.AddMinutes(1)) == "1 minute ago", "exactly 1 minute uses singular 'minute'");
        Check(MCPClientConfigTracker.FormatRelativeTime(now, now.AddMinutes(5)) == "5 minutes ago", "5 minutes uses plural 'minutes'");
        Check(MCPClientConfigTracker.FormatRelativeTime(now, now.AddHours(1)) == "1 hour ago", "exactly 1 hour uses singular 'hour'");
        Check(MCPClientConfigTracker.FormatRelativeTime(now, now.AddHours(3)) == "3 hours ago", "3 hours uses plural 'hours'");
        Check(MCPClientConfigTracker.FormatRelativeTime(now, now.AddDays(1)) == "1 day ago", "exactly 1 day uses singular 'day'");
        Check(MCPClientConfigTracker.FormatRelativeTime(now, now.AddDays(2)) == "2 days ago", "2 days uses plural 'days'");
        Check(MCPClientConfigTracker.FormatRelativeTime(now, now.AddDays(45)) == now.ToLocalTime().ToString("yyyy-MM-dd"),
            "45 days falls back to an absolute date rather than an increasingly-useless relative phrase");

        // Clock-skew guard: a "configured at" timestamp that's (impossibly) in the
        // future relative to "now" must never render as a negative age.
        Check(MCPClientConfigTracker.FormatRelativeTime(now.AddMinutes(5), now) == "just now",
            "a configuredAt timestamp after 'now' (clock skew) never shows a negative age, clamps to 'just now'");
    }

    private static void TestMiniJsonParsing()
    {
        // --- A simple flat object ---
        bool ok1 = MCPMiniJson.TryExtractObjectEntries("{\"a\": \"1\", \"b\": \"2\"}", out var entries1, out var err1);
        Check(ok1 && entries1.Count == 2 && entries1["a"] == "\"1\"" && entries1["b"] == "\"2\"",
            "TryExtractObjectEntries parses a simple flat object correctly: " + err1);

        // --- Nested object/array values are captured whole, as opaque raw text, not recursed into ---
        bool ok2 = MCPMiniJson.TryExtractObjectEntries("{\"server\": {\"command\": \"python\", \"args\": [\"-m\", \"x\"]}}", out var entries2, out var err2);
        Check(ok2 && entries2["server"] == "{\"command\": \"python\", \"args\": [\"-m\", \"x\"]}",
            "TryExtractObjectEntries captures a nested object's ENTIRE raw text as one opaque value, without needing to understand its internals: " + entries2.GetValueOrDefault("server"));

        // --- Braces/brackets INSIDE quoted strings must not be mistaken for structural nesting ---
        bool ok3 = MCPMiniJson.TryExtractObjectEntries("{\"note\": \"contains a { brace } and a [ bracket ]\"}", out var entries3, out var err3);
        Check(ok3 && entries3["note"] == "\"contains a { brace } and a [ bracket ]\"",
            "TryExtractObjectEntries does not miscount braces/brackets that appear INSIDE a quoted string value: " + err3);

        // --- Escaped quotes inside a string must not be mistaken for the string's end ---
        bool ok4 = MCPMiniJson.TryExtractObjectEntries("{\"note\": \"has an \\\"escaped quote\\\" inside\"}", out var entries4, out var err4);
        Check(ok4 && entries4.ContainsKey("note"), "TryExtractObjectEntries correctly handles an escaped quote inside a string value: " + err4);

        // --- Empty object ---
        bool ok5 = MCPMiniJson.TryExtractObjectEntries("{}", out var entries5, out var err5);
        Check(ok5 && entries5.Count == 0, "TryExtractObjectEntries handles an empty object correctly");

        // --- Empty/null input is treated as a valid empty object (fresh-start case) ---
        bool ok6 = MCPMiniJson.TryExtractObjectEntries("", out var entries6, out var err6);
        Check(ok6 && entries6.Count == 0, "TryExtractObjectEntries treats empty input as a valid empty object, not an error");

        // --- Genuinely malformed JSON is refused, not silently misparsed ---
        bool ok7 = MCPMiniJson.TryExtractObjectEntries("{\"a\": \"1\"", out var entries7, out var err7); // missing closing brace
        Check(!ok7 && err7 != null, "TryExtractObjectEntries refuses genuinely malformed JSON (missing closing brace) with a clear error: " + err7);

        bool ok8 = MCPMiniJson.TryExtractObjectEntries("not even json", out var entries8, out var err8);
        Check(!ok8 && err8 != null, "TryExtractObjectEntries refuses non-JSON input with a clear error: " + err8);
    }

    private static void TestMcpServersJsonWriterMerging()
    {
        var args = new List<string> { "-m", "unity_mcp_server.server" };
        var env = new Dictionary<string, string> { ["UNITY_MCP_PROJECT_ROOT"] = "/Users/dev/MyGame", ["PYTHONPATH"] = "/Users/dev/python-server" };
        var entryJson = MCPMcpServersJsonWriter.BuildServerEntryJson("/Users/dev/python-server/.venv/bin/python3", args, env);

        Check(entryJson.Contains("\"command\": \"/Users/dev/python-server/.venv/bin/python3\"")
              && entryJson.Contains("\"args\": [\"-m\", \"unity_mcp_server.server\"]")
              && entryJson.Contains("\"UNITY_MCP_PROJECT_ROOT\": \"/Users/dev/MyGame\"")
              && entryJson.Contains("\"PYTHONPATH\": \"/Users/dev/python-server\""),
            "BuildServerEntryJson produces the expected command/args/env shape: " + entryJson);

        // --- Fresh file (no existing content) ---
        bool ok1 = MCPMcpServersJsonWriter.TryMerge(null, "unity-MyGame", entryJson, out var content1, out var err1);
        Check(ok1 && content1.Contains("\"mcpServers\"") && content1.Contains("\"unity-MyGame\""),
            "TryMerge on null/empty existing content creates a fresh file with just our entry: " + err1);

        // --- THE critical real-world case: an existing file with ANOTHER server already
        // configured (for some other tool) must be preserved untouched, not clobbered. ---
        const string existingWithOtherServer =
            "{\n  \"mcpServers\": {\n    \"some-other-tool\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"@some/other-tool\"]\n    }\n  }\n}\n";
        bool ok2 = MCPMcpServersJsonWriter.TryMerge(existingWithOtherServer, "unity-MyGame", entryJson, out var content2, out var err2);
        Check(ok2 && content2.Contains("\"some-other-tool\"") && content2.Contains("\"@some/other-tool\"") && content2.Contains("\"unity-MyGame\""),
            "TryMerge preserves an existing, unrelated server entry untouched while adding ours: " + err2);

        // --- Re-configuring (our own entry already present) OVERWRITES just our entry,
        // not duplicates it -- this is what makes clicking Configure again after changing
        // the Python server path actually take effect. ---
        var updatedEntryJson = MCPMcpServersJsonWriter.BuildServerEntryJson("/new/path/.venv/bin/python3", args, env);
        bool ok3 = MCPMcpServersJsonWriter.TryMerge(content2, "unity-MyGame", updatedEntryJson, out var content3, out var err3);
        Check(ok3 && content3.Contains("/new/path/.venv/bin/python3") && !content3.Contains("/Users/dev/python-server/.venv/bin/python3")
              && content3.Contains("\"some-other-tool\""),
            "Re-merging with an updated entry for the SAME server name replaces it (not duplicates it), while still preserving the unrelated entry: " + err3);

        // --- Malformed existing content is refused, not silently overwritten ---
        bool ok4 = MCPMcpServersJsonWriter.TryMerge("{ this is not valid json", "unity-MyGame", entryJson, out var content4, out var err4);
        Check(!ok4 && content4 == null && err4 != null,
            "TryMerge refuses to touch a file with malformed existing JSON, rather than risk destroying content it can't understand: " + err4);

        // --- IsConfigured ---
        Check(MCPMcpServersJsonWriter.IsConfigured(content2, "unity-MyGame"), "IsConfigured finds our server after a real merge");
        Check(!MCPMcpServersJsonWriter.IsConfigured(content2, "unity-SomeOtherGame"), "IsConfigured correctly reports false for a server name that isn't present");
        Check(!MCPMcpServersJsonWriter.IsConfigured(null, "unity-MyGame"), "IsConfigured handles null content without throwing");
    }

    private static void TestCodexTomlWriterMerging()
    {
        var args = new List<string> { "-m", "unity_mcp_server.server" };
        var env = new Dictionary<string, string> { ["UNITY_MCP_PROJECT_ROOT"] = "/Users/dev/MyGame", ["PYTHONPATH"] = "/Users/dev/python-server" };
        var section = MCPCodexTomlWriter.BuildServerSection("unity-MyGame", "/Users/dev/python-server/.venv/bin/python3", args, env);

        Check(section.Contains("[mcp_servers.unity-MyGame]")
              && section.Contains("command = \"/Users/dev/python-server/.venv/bin/python3\"")
              && section.Contains("args = [\"-m\", \"unity_mcp_server.server\"]")
              && section.Contains("[mcp_servers.unity-MyGame.env]")
              && section.Contains("UNITY_MCP_PROJECT_ROOT = \"/Users/dev/MyGame\""),
            "BuildServerSection produces the expected TOML shape: " + section);

        // --- Fresh file ---
        var content1 = MCPCodexTomlWriter.Merge(null, "unity-MyGame", section);
        Check(content1 == section, "Merge on null existing content just returns the section as-is");

        // --- Appending alongside an existing, unrelated section ---
        const string existingWithOtherServer = "[mcp_servers.some-other-tool]\ncommand = \"npx\"\nargs = [\"-y\", \"@some/other-tool\"]\n";
        var content2 = MCPCodexTomlWriter.Merge(existingWithOtherServer, "unity-MyGame", section);
        Check(content2.Contains("[mcp_servers.some-other-tool]") && content2.Contains("@some/other-tool") && content2.Contains("[mcp_servers.unity-MyGame]"),
            "Merge appends our section while preserving an existing unrelated one untouched: " + content2);

        // --- Re-configuring (span-replace): our section already present gets REPLACED in
        // place, not duplicated, and anything before/after it is preserved exactly. ---
        var updatedSection = MCPCodexTomlWriter.BuildServerSection("unity-MyGame", "/new/path/.venv/bin/python3", args, env);
        var content3 = MCPCodexTomlWriter.Merge(content2, "unity-MyGame", updatedSection);
        Check(content3.Contains("/new/path/.venv/bin/python3") && !content3.Contains("/Users/dev/python-server/.venv/bin/python3"),
            "Re-merging replaces our section's content with the updated values: " + content3);
        Check(content3.Contains("[mcp_servers.some-other-tool]") && content3.Contains("@some/other-tool"),
            "Re-merging (span-replace) still preserves the OTHER, unrelated section completely untouched: " + content3);
        int occurrences = content3.Split(new[] { "[mcp_servers.unity-MyGame]" }, StringSplitOptions.None).Length - 1;
        Check(occurrences == 1, "Re-merging replaces our section in place rather than duplicating it (found " + occurrences + " occurrences of our header)");

        // --- A server section that comes AFTER ours in the file must also survive a
        // span-replace of OUR section (proves the boundary-finding doesn't overrun). ---
        const string otherAfterOurs = "[mcp_servers.unity-MyGame]\ncommand = \"old\"\nargs = []\n\n[mcp_servers.another-tool]\ncommand = \"another\"\nargs = []\n";
        var content4 = MCPCodexTomlWriter.Merge(otherAfterOurs, "unity-MyGame", section);
        Check(content4.Contains("[mcp_servers.another-tool]") && content4.Contains("command = \"another\""),
            "Re-merging our section correctly stops at the NEXT section (one that comes after ours), leaving it completely intact: " + content4);

        // --- IsConfigured ---
        Check(MCPCodexTomlWriter.IsConfigured(content2, "unity-MyGame"), "IsConfigured finds our section after a real merge");
        Check(!MCPCodexTomlWriter.IsConfigured(content2, "unity-SomeOtherGame"), "IsConfigured correctly reports false for a server name that isn't present");
        Check(!MCPCodexTomlWriter.IsConfigured(null, "unity-MyGame"), "IsConfigured handles null content without throwing");
    }

    private static void TestEndToEndConfigFileWriting()
    {
        // Real disk I/O, exercising the exact sequence MCPSetupWindow.Configure() runs,
        // for a JSON client (Cursor) and the TOML client (Codex) -- including running it
        // TWICE to prove idempotent re-configure actually updates the file in place.
        var tempRoot = Path.Combine(Path.GetTempPath(), "mcp_config_e2e_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var serverName = "unity-TestProject";
            var args = MCPServerEntryBuilder.Args();

            // --- Cursor (JSON) ---
            var cursorPath = MCPMcpConfigTargets.AbsolutePath(MCPClientKind.Cursor, tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(cursorPath));
            File.WriteAllText(cursorPath, "{\"mcpServers\": {\"pre-existing\": {\"command\": \"npx\", \"args\": []}}}");

            var env1 = MCPServerEntryBuilder.Env(tempRoot, "/Users/dev/python-server");
            var entry1 = MCPMcpServersJsonWriter.BuildServerEntryJson("/Users/dev/python-server/.venv/bin/python3", args, env1);
            MCPMcpServersJsonWriter.TryMerge(File.ReadAllText(cursorPath), serverName, entry1, out var written1, out _);
            File.WriteAllText(cursorPath, written1);

            var onDisk1 = File.ReadAllText(cursorPath);
            Check(onDisk1.Contains("pre-existing") && onDisk1.Contains(serverName) && onDisk1.Contains("/Users/dev/python-server/.venv/bin/python3"),
                "End-to-end: a real file written to disk for Cursor contains both the pre-existing entry and our new one");

            // Re-configure with a different path -- proves the real ON-DISK file gets updated, not just in-memory.
            var env2 = MCPServerEntryBuilder.Env(tempRoot, "/updated/python-server");
            var entry2 = MCPMcpServersJsonWriter.BuildServerEntryJson("/updated/python-server/.venv/bin/python3", args, env2);
            MCPMcpServersJsonWriter.TryMerge(File.ReadAllText(cursorPath), serverName, entry2, out var written2, out _);
            File.WriteAllText(cursorPath, written2);

            var onDisk2 = File.ReadAllText(cursorPath);
            Check(onDisk2.Contains("/updated/python-server/.venv/bin/python3") && !onDisk2.Contains("/Users/dev/python-server/.venv/bin/python3") && onDisk2.Contains("pre-existing"),
                "End-to-end: re-writing the real file on disk updates our entry in place and still preserves the pre-existing one");

            // --- Codex (TOML) ---
            var codexPath = MCPMcpConfigTargets.AbsolutePath(MCPClientKind.Codex, tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(codexPath));
            File.WriteAllText(codexPath, "[mcp_servers.pre-existing]\ncommand = \"npx\"\nargs = []\n");

            var section1 = MCPCodexTomlWriter.BuildServerSection(serverName, "/Users/dev/python-server/.venv/bin/python3", args, env1);
            var codexWritten1 = MCPCodexTomlWriter.Merge(File.ReadAllText(codexPath), serverName, section1);
            File.WriteAllText(codexPath, codexWritten1);

            var codexOnDisk1 = File.ReadAllText(codexPath);
            Check(codexOnDisk1.Contains("[mcp_servers.pre-existing]") && codexOnDisk1.Contains($"[mcp_servers.{serverName}]"),
                "End-to-end: a real .codex/config.toml written to disk contains both the pre-existing section and our new one");

            var section2 = MCPCodexTomlWriter.BuildServerSection(serverName, "/updated/python-server/.venv/bin/python3", args, env2);
            var codexWritten2 = MCPCodexTomlWriter.Merge(File.ReadAllText(codexPath), serverName, section2);
            File.WriteAllText(codexPath, codexWritten2);

            var codexOnDisk2 = File.ReadAllText(codexPath);
            Check(codexOnDisk2.Contains("/updated/python-server/.venv/bin/python3") && !codexOnDisk2.Contains("/Users/dev/python-server/.venv/bin/python3")
                  && codexOnDisk2.Contains("[mcp_servers.pre-existing]"),
                "End-to-end: re-writing the real .codex/config.toml updates our section in place and still preserves the pre-existing one");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void TestInstanceConflictDetection()
    {
        const int currentPid = 1000;
        const int currentPort = 45678;

        // --- No session file on disk at all: nothing to compare against, no conflict ---
        var noFile = MCPInstanceConflictDetector.Evaluate(currentPid, currentPort, null, pid => true);
        Check(!noFile.HasConflict, "no session.json on disk at all is not treated as a conflict: " + noFile.Message);

        // --- session.json belongs to THIS process (normal case, e.g. right after our own Write()) ---
        var ownSession = new MCPSessionSnapshot { Pid = currentPid, Port = currentPort, StartedAt = "now" };
        var ownResult = MCPInstanceConflictDetector.Evaluate(currentPid, currentPort, ownSession, pid => true);
        Check(!ownResult.HasConflict, "session.json matching this process's own PID is not a conflict: " + ownResult.Message);

        // --- session.json belongs to a DIFFERENT pid, but that process is no longer running ---
        var stalePid = 2000;
        var staleSession = new MCPSessionSnapshot { Pid = stalePid, Port = 45679, StartedAt = "earlier" };
        var staleResult = MCPInstanceConflictDetector.Evaluate(currentPid, currentPort, staleSession, pid => false);
        Check(!staleResult.HasConflict, "session.json from a different PID that's no longer alive is stale, not a conflict: " + staleResult.Message);

        // --- session.json belongs to a different, LIVE pid, but claims the SAME port this
        // process is bound to -- impossible for two real listeners, so treated as a
        // transient mid-write read, not a genuine second listener ---
        var samePortSession = new MCPSessionSnapshot { Pid = 3000, Port = currentPort, StartedAt = "now" };
        var samePortResult = MCPInstanceConflictDetector.Evaluate(currentPid, currentPort, samePortSession, pid => true);
        Check(!samePortResult.HasConflict, "a different live PID claiming the SAME port as this process is treated as a transient read, not a real conflict: " + samePortResult.Message);

        // --- THE actual reported incident: a different, LIVE pid, on a DIFFERENT port ---
        var conflictingPid = 4000;
        var conflictingSession = new MCPSessionSnapshot { Pid = conflictingPid, Port = 45680, StartedAt = "2026-01-01T00:00:00Z" };
        var conflictResult = MCPInstanceConflictDetector.Evaluate(currentPid, currentPort, conflictingSession, pid => pid == conflictingPid);
        Check(conflictResult.HasConflict, "a different, live PID on a genuinely different port IS flagged as a real conflict: " + conflictResult.Message);
        Check(conflictResult.OtherPid == conflictingPid && conflictResult.OtherPort == 45680,
            "the conflict info carries the actual other PID and port for display: " + conflictResult.Message);
        Check(conflictResult.Message.Contains("4000") && conflictResult.Message.Contains("45680"),
            "the conflict message names the specific PID and port, not just 'a conflict exists': " + conflictResult.Message);

        // --- The isProcessAlive delegate is actually consulted with the RIGHT pid, not
        // some hardcoded/ignored value -- verified by having it return false only for the
        // specific pid under test and confirming that flips the result. ---
        var otherPid = 5000;
        var otherSession = new MCPSessionSnapshot { Pid = otherPid, Port = 45681, StartedAt = "now" };
        var aliveResult = MCPInstanceConflictDetector.Evaluate(currentPid, currentPort, otherSession, pid => pid == otherPid);
        var deadResult = MCPInstanceConflictDetector.Evaluate(currentPid, currentPort, otherSession, pid => pid != otherPid);
        Check(aliveResult.HasConflict && !deadResult.HasConflict,
            "the isProcessAlive delegate is actually consulted with the specific PID being checked, not ignored");

        // --- Smoke test of the real wrapper: DetectReal() runs against actual OS state
        // (this test process's own PID) without throwing, even with no real session.json
        // present in this stub environment. ---
        var real = MCPInstanceConflictDetector.DetectReal(currentPort);
        Check(real != null, "DetectReal() runs against real OS process state without throwing");
    }

    private static void TestPathGuardSymlinkDetection()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mcp_pathguard_test_" + Guid.NewGuid().ToString("N"));
        var assetsDir = Path.Combine(tempRoot, "Assets");
        var outsideDir = Path.Combine(Path.GetTempPath(), "mcp_pathguard_outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(assetsDir);
        Directory.CreateDirectory(outsideDir);

        var symlinkPath = Path.Combine(assetsDir, "Escape");
        bool symlinkCreated = false;

        try
        {
            // --- Baseline regression check: an entirely normal path (no symlinks
            // anywhere) still resolves successfully after this change. ---
            var normalDir = Path.Combine(assetsDir, "Normal");
            Directory.CreateDirectory(normalDir);
            bool normalOk = MCPPathGuard.TryResolveWithinAssets(tempRoot, "Normal/file.txt", out var normalPath, out var normalError);
            Check(normalOk && normalError == null, "a normal path with no symlinks involved still resolves successfully (regression check): " + normalError);

            symlinkCreated = TryCreateSymlink(symlinkPath, outsideDir);
            if (!symlinkCreated)
            {
                Console.WriteLine(
                    "[SKIP] could not create a real symlink on this system (no 'ln' binary, insufficient permission, or " +
                    "unsupported platform) -- the symlink-traversal detection itself was not exercised for real here, " +
                    "only the non-symlink baseline above. Rerun on a Unix-like system with 'ln' available for full coverage.");
                return;
            }

            File.WriteAllText(Path.Combine(outsideDir, "secret.txt"), "should not be reachable via Assets/");

            // --- The actual vulnerability being closed: a symlink INSIDE Assets/
            // pointing outside it, traversed through by a relative path that looks
            // perfectly normal. ---
            bool traversalOk = MCPPathGuard.TryResolveWithinAssets(tempRoot, "Escape/secret.txt", out var traversalPath, out var traversalError);
            Check(!traversalOk && traversalError != null && traversalError.ToLowerInvariant().Contains("symlink"),
                "a real symlink inside Assets/ pointing outside it is detected and refused when traversed through, not silently followed: " + traversalError);

            // --- The symlink itself as the final path component (not traversed
            // through en route to something else, but IS the direct target). ---
            bool leafOk = MCPPathGuard.TryResolveWithinAssets(tempRoot, "Escape", out var leafPath, out var leafError);
            Check(!leafOk && leafError != null && leafError.ToLowerInvariant().Contains("symlink"),
                "the symlink itself, as the final path component, is also refused: " + leafError);
        }
        finally
        {
            // Remove the symlink itself FIRST, via the same external tool that created
            // it, before any recursive directory delete runs -- recursive delete
            // behavior around symlinked directories has varied across .NET/Mono
            // versions (some older behavior would follow the link and delete the
            // TARGET's contents, which would be a real, dangerous bug in a cleanup
            // routine, not just in the guard being tested).
            if (symlinkCreated) TryRemovePath(symlinkPath);
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(outsideDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static bool TryCreateSymlink(string linkPath, string targetPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ln",
                Arguments = $"-s \"{targetPath}\" \"{linkPath}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using (var proc = Process.Start(psi))
            {
                proc.WaitForExit(5000);
                return proc.ExitCode == 0 && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void TryRemovePath(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "rm",
                Arguments = $"-f \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var proc = Process.Start(psi))
            {
                proc.WaitForExit(5000);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static void TestTypeResolverCachingAndAmbiguity()
    {
        MCPTypeResolver.ClearCacheForTest();

        // --- A genuinely ambiguous short name (two real types, different namespaces,
        // both compiled into this very test binary) is refused, not silently resolved
        // to whichever one happened to be enumerated first. ---
        bool ambiguousOk = MCPTypeResolver.TryResolve("AmbiguousTestType", out var ambiguousType, out var ambiguousError);
        Check(!ambiguousOk && ambiguousType == null,
            "a genuinely ambiguous short type name is refused rather than silently resolved: " + ambiguousError);
        Check(ambiguousError != null && ambiguousError.Contains("ambiguous")
              && ambiguousError.Contains("UnityMCP.TestNamespaceA.AmbiguousTestType")
              && ambiguousError.Contains("UnityMCP.TestNamespaceB.AmbiguousTestType"),
            "the ambiguity error names BOTH candidate types by full name, not a generic 'not found': " + ambiguousError);

        // --- The SAME ambiguous short name, but fully qualified, resolves unambiguously. ---
        bool qualifiedOk = MCPTypeResolver.TryResolve("UnityMCP.TestNamespaceA.AmbiguousTestType", out var qualifiedType, out var qualifiedError);
        Check(qualifiedOk && qualifiedType != null && qualifiedType.FullName == "UnityMCP.TestNamespaceA.AmbiguousTestType",
            "the same ambiguous name, fully qualified, resolves unambiguously to the correct specific type: " + qualifiedError);

        // --- A genuinely nonexistent type still says "not found", not "ambiguous". ---
        bool missingOk = MCPTypeResolver.TryResolve("ThisTypeDefinitelyDoesNotExistAnywhere", out var missingType, out var missingError);
        Check(!missingOk && missingType == null && missingError != null && missingError.Contains("not found") && !missingError.Contains("ambiguous"),
            "a genuinely nonexistent type name is reported as 'not found', distinct from an ambiguity error: " + missingError);

        // --- A normal, unambiguous, real type still resolves correctly (regression). ---
        bool normalOk = MCPTypeResolver.TryResolve("TestTool", out var normalType, out var normalError);
        Check(normalOk && normalType != null && normalType.Name == "TestTool",
            "a normal, unambiguous type name still resolves correctly: " + normalError);

        // --- Cache consistency: resolving the same name twice returns the identical
        // Type reference both times (true regardless of caching for a real reflected
        // Type, but confirms the cached path doesn't somehow return something different). ---
        MCPTypeResolver.TryResolve("TestTool", out var firstCall, out _);
        MCPTypeResolver.TryResolve("TestTool", out var secondCall, out _);
        Check(ReferenceEquals(firstCall, secondCall), "resolving the same type name twice (cached path) returns the identical Type reference both times");

        // --- The old plain Resolve() convenience wrapper still works for callers that
        // don't need the ambiguous/not-found distinction. ---
        var viaOldApi = MCPTypeResolver.Resolve("TestTool");
        Check(viaOldApi != null && viaOldApi.Name == "TestTool", "the plain Resolve() wrapper still works for simple callers");

        // --- resolve_type tool: a thin registry-facing wrapper over MCPTypeResolver,
        // exercised through Invoke() (not calling MCPTypeResolver directly) so the
        // actual tool-call path -- schema binding included -- is what's verified. ---
        var ctx = new MCPToolContext { RequestId = "resolve-type-test" };
        var resolveOk = MCPToolRegistry.Invoke("resolve_type", new Dictionary<string, object> { ["typeName"] = "MonoBehaviour" }, ctx);
        Check(resolveOk.Ok, "resolve_type succeeds for a real short type name: " + resolveOk.Error);

        var resolveFail = MCPToolRegistry.Invoke("resolve_type", new Dictionary<string, object> { ["typeName"] = "ThisTypeDefinitelyDoesNotExistAnywhere" }, ctx);
        Check(!resolveFail.Ok && resolveFail.Error != null && resolveFail.Error.Contains("not found"),
            "resolve_type fails cleanly (not throws) for a nonexistent type: " + resolveFail.Error);
    }

    private static void TestHierarchyCacheObjectChangeEvents()
    {
        // MCPHierarchyCache is static, shared state across this whole test binary run --
        // an earlier test (TestPhase4Performance) already exercised it, so this test
        // can't assume it's starting from a cold, never-built cache. Every assertion
        // below is relative to a build-count captured at each step, not an absolute
        // count, so it's correct regardless of whatever state the cache was left in.
        int buildCount = 0;
        Func<object> builder = () =>
        {
            buildCount++;
            return new { call = buildCount };
        };

        // Force a known starting state via the existing hierarchyChanged hook (already
        // proven reliable in TestPhase4Performance), so this test's own first
        // GetOrBuild call is guaranteed to actually invoke the builder rather than
        // silently hitting a cache some earlier test warmed up.
        UnityEditor.EditorApplication.RaiseHierarchyChangedForTest();
        var first = MCPHierarchyCache.GetOrBuild(builder);
        int countAfterFirst = buildCount;
        Check(countAfterFirst == 1, "after forcing invalidation, the next GetOrBuild call actually invokes the builder exactly once: buildCount=" + buildCount);

        var second = MCPHierarchyCache.GetOrBuild(builder);
        Check(buildCount == countAfterFirst && ReferenceEquals(first, second), "a second call with no invalidation in between is a cache hit (no new build)");

        // --- The actual fix: a published change with length > 0 invalidates, even
        // though hierarchyChanged itself never fired -- this is exactly the case a
        // rename (via set_component_field on Transform.name, or a manual Editor edit)
        // falls into, which hierarchyChanged alone would have missed. ---
        UnityEditor.ObjectChangeEvents.RaisePublishedForTest(length: 1);
        var third = MCPHierarchyCache.GetOrBuild(builder);
        Check(buildCount == countAfterFirst + 1,
            "a published ObjectChangeEvents change (length > 0) invalidates the cache on its own, without hierarchyChanged needing to fire: count went from " + countAfterFirst + " to " + buildCount);
        Check(!ReferenceEquals(first, third), "post-invalidation build via ObjectChangeEvents returns a new instance, not the stale cached one");

        int countAfterThird = buildCount;

        // --- A published event with length == 0 (no actual changes) does NOT
        // invalidate -- matches the production code's explicit `if (stream.length > 0)` guard. ---
        UnityEditor.ObjectChangeEvents.RaisePublishedForTest(length: 0);
        var fourth = MCPHierarchyCache.GetOrBuild(builder);
        Check(buildCount == countAfterThird && ReferenceEquals(third, fourth), "a published event with zero actual changes does NOT invalidate the cache");
    }
}
