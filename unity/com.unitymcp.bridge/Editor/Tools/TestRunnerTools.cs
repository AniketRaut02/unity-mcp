using System.Collections.Generic;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityMCP;
using UnityMCP.Support;

namespace UnityMCP.Tools
{
    public static class TestRunnerTools
    {
        [MCPTool("list_tests", "Lists available Unity Test Framework tests for a given mode without running them.", group: "testing")]
        public static MCPResult ListTests(
            MCPToolContext ctx,
            [MCPParam("\"edit\" or \"play\". Defaults to \"edit\".")] string mode = "edit",
            [MCPParam("Maximum time to wait for the test list to be retrieved, in seconds. Defaults to 10.")] float timeoutSeconds = 10f)
        {
            if (!TryParseTestMode(mode, out var testMode, out var modeError))
                return MCPResult.Fail(modeError);

            ITestAdaptor root = null;
            var done = false;

            MCPTestRunnerCache.RetrieveTestList(testMode, result =>
            {
                root = result;
                done = true;
            });

            var start = EditorApplication.timeSinceStartup;
            while (!done)
            {
                if (EditorApplication.timeSinceStartup - start > timeoutSeconds)
                    return MCPResult.Fail($"Timed out after {timeoutSeconds}s retrieving the test list.");
                System.Threading.Thread.Sleep(50);
            }

            var tests = new List<object>();
            CollectLeafTests(root, tests);

            return MCPResult.Success(new { mode, count = tests.Count, tests });
        }

        [MCPTool("run_edit_mode_tests", "Runs Unity Test Framework EditMode tests (optionally filtered by full test/fixture name) and waits for completion, returning a pass/fail summary and details of any failures.", group: "testing", latencyTier: MCPLatencyTier.Slow)]
        public static MCPResult RunEditModeTests(
            MCPToolContext ctx,
            [MCPParam("Full test or fixture name to filter to, e.g. \"MyNamespace.MyFixture\" or \"MyNamespace.MyFixture.MyTest\". Omit to run all EditMode tests.")] string testFilter = null,
            [MCPParam("Maximum time to wait for the run to finish, in seconds. Defaults to 120.")] float timeoutSeconds = 120f)
        {
            return RunAndWait(TestMode.EditMode, testFilter, timeoutSeconds);
        }

        [MCPTool("run_play_mode_tests", "Runs Unity Test Framework PlayMode tests (optionally filtered by full test/fixture name) and waits for completion, returning a pass/fail summary and details of any failures. Enters Play mode automatically as needed.", group: "testing", latencyTier: MCPLatencyTier.Slow)]
        public static MCPResult RunPlayModeTests(
            MCPToolContext ctx,
            [MCPParam("Full test or fixture name to filter to, e.g. \"MyNamespace.MyFixture\" or \"MyNamespace.MyFixture.MyTest\". Omit to run all PlayMode tests.")] string testFilter = null,
            [MCPParam("Maximum time to wait for the run to finish, in seconds. Defaults to 120.")] float timeoutSeconds = 120f)
        {
            return RunAndWait(TestMode.PlayMode, testFilter, timeoutSeconds);
        }

        private static MCPResult RunAndWait(TestMode mode, string testFilter, float timeoutSeconds)
        {
            if (MCPTestRunnerCache.IsRunning)
                return MCPResult.Fail("A test run is already in progress. Wait for it to finish before starting another.");

            MCPTestRunnerCache.StartRun(mode, testFilter);

            var start = EditorApplication.timeSinceStartup;
            while (MCPTestRunnerCache.IsRunning)
            {
                if (EditorApplication.timeSinceStartup - start > timeoutSeconds)
                    return MCPResult.Fail($"Timed out after {timeoutSeconds}s waiting for the test run to finish. It may still be running — check again with a longer timeout, or via the Test Runner window.");
                System.Threading.Thread.Sleep(100);
            }

            var summary = MCPTestRunnerCache.TryGetResult();
            if (summary == null)
                return MCPResult.Fail("Test run reported finished, but no result summary was found. Check the Test Runner window directly.");

            return MCPResult.Success(new
            {
                passed = summary.rootResultState == "Passed",
                resultState = summary.rootResultState,
                passCount = summary.passCount,
                failCount = summary.failCount,
                skipCount = summary.skipCount,
                inconclusiveCount = summary.inconclusiveCount,
                durationSeconds = summary.durationSeconds,
                failures = summary.failures
            });
        }

        private static void CollectLeafTests(ITestAdaptor node, List<object> into)
        {
            if (node == null) return;

            if (!node.IsSuite)
            {
                into.Add(new { name = node.Name, fullName = node.FullName });
                return;
            }

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                    CollectLeafTests(child, into);
            }
        }

        private static bool TryParseTestMode(string mode, out TestMode testMode, out string error)
        {
            switch (mode?.ToLowerInvariant())
            {
                case "edit":
                case "editmode":
                    testMode = TestMode.EditMode;
                    error = null;
                    return true;
                case "play":
                case "playmode":
                    testMode = TestMode.PlayMode;
                    error = null;
                    return true;
                default:
                    testMode = default;
                    error = $"Invalid mode '{mode}'. Use \"edit\" or \"play\".";
                    return false;
            }
        }
    }
}
