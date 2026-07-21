using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityMCP.Support
{
    /// <summary>
    /// Wraps TestRunnerApi for the test-running tools. Run state
    /// (IsRunning / result summary) is tracked via SessionState rather than
    /// plain static fields, because a domain reload can happen mid-run
    /// (PlayMode tests enter Play mode; either that or the run itself can
    /// trigger a recompile) — SessionState survives a reload within the
    /// same Editor session, plain statics don't. Callback *registration*
    /// still needs [InitializeOnLoad] re-subscription regardless, since
    /// Unity's own docs note delegate registrations don't survive a reload
    /// even though SessionState data does.
    ///
    /// Uses TestRunnerApi.RetrieveTestList (obsolete in Test Framework 2.0+
    /// in favor of RetrieveTestTree) deliberately, for compatibility with
    /// older Test Framework versions — expect a harmless CS0618 warning on
    /// newer ones.
    /// </summary>
    [InitializeOnLoad]
    internal static class MCPTestRunnerCache
    {
        private const string RunningKey = "MCPTestRunner.Running";
        private const string ResultJsonKey = "MCPTestRunner.ResultJson";

        [Serializable]
        internal class TestResultSummary
        {
            public string fullName;
            public string resultState;
            public double durationSeconds;
            public string message;
            public string stackTrace;
        }

        [Serializable]
        internal class RunSummary
        {
            public bool finished;
            public string rootFullName;
            public string rootResultState;
            public int passCount;
            public int failCount;
            public int skipCount;
            public int inconclusiveCount;
            public double durationSeconds;
            public TestResultSummary[] failures;
        }

        private static TestRunnerApi _api;
        private static TestRunnerApi Api => _api != null ? _api : (_api = ScriptableObject.CreateInstance<TestRunnerApi>());

        static MCPTestRunnerCache()
        {
            Api.RegisterCallbacks(new Callbacks());
        }

        internal static bool IsRunning => SessionState.GetBool(RunningKey, false);

        internal static void StartRun(TestMode mode, string testFilter)
        {
            SessionState.SetBool(RunningKey, true);
            SessionState.SetString(ResultJsonKey, "");

            var filter = new Filter { testMode = mode };
            if (!string.IsNullOrEmpty(testFilter))
                filter.testNames = new[] { testFilter };

            Api.Execute(new ExecutionSettings(filter));
        }

        internal static RunSummary TryGetResult()
        {
            var json = SessionState.GetString(ResultJsonKey, "");
            return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<RunSummary>(json);
        }

        internal static void RetrieveTestList(TestMode mode, Action<ITestAdaptor> onComplete)
        {
#pragma warning disable CS0618 // RetrieveTestList is obsolete in newer Test Framework versions; kept for broader compatibility.
            Api.RetrieveTestList(mode, onComplete);
#pragma warning restore CS0618
        }

        private class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                var failures = new List<TestResultSummary>();
                CollectFailures(result, failures);

                var summary = new RunSummary
                {
                    finished = true,
                    rootFullName = result.FullName,
                    rootResultState = result.ResultState,
                    passCount = result.PassCount,
                    failCount = result.FailCount,
                    skipCount = result.SkipCount,
                    inconclusiveCount = result.InconclusiveCount,
                    durationSeconds = result.Duration,
                    failures = failures.ToArray()
                };

                SessionState.SetString(ResultJsonKey, JsonUtility.ToJson(summary));
                SessionState.SetBool(RunningKey, false);
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result) { }

            private static void CollectFailures(ITestResultAdaptor node, List<TestResultSummary> into)
            {
                if (!node.HasChildren)
                {
                    if (node.TestStatus == TestStatus.Failed)
                    {
                        into.Add(new TestResultSummary
                        {
                            fullName = node.FullName,
                            resultState = node.ResultState,
                            durationSeconds = node.Duration,
                            message = node.Message,
                            stackTrace = node.StackTrace
                        });
                    }
                    return;
                }

                foreach (var child in node.Children)
                    CollectFailures(child, into);
            }
        }
    }
}
