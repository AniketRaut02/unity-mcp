using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityMCP.Security
{
    /// <summary>
    /// Human approval for danger-tier calls, given in the Editor (MCPApprovalWindow), never by
    /// the AI itself.
    ///
    /// Flow (no blocking, so no client or server timeout is ever involved):
    ///   1. The agent calls a danger-tier tool. It does not run; the call returns a normal
    ///      result {status: "awaiting_approval", ...} and the request appears in the window.
    ///   2. A human clicks Approve or Deny.
    ///   3. The agent's next call with the SAME tool and SAME arguments runs once (approved) or
    ///      is told it was denied. Approvals have no time limit; each is used exactly once.
    /// "Allow for this session" lets every later call to that one tool run without asking
    /// until the Editor closes.
    ///
    /// State lives in SessionState so a domain reload between the approval and the agent's
    /// retry loses nothing. Main thread only.
    /// </summary>
    public static class MCPApprovalGate
    {
        private const string RequestsKey = "UnityMCP.ApprovalRequests";
        private const string SessionToolsKey = "UnityMCP.ApprovalSessionTools";

        public enum Decision { Pending, Approved, Denied }

        public class Request
        {
            public string key;
            public string tool;
            public string argsJson;
            public Decision decision;
            public string requestedAt;
        }

        public static event Action Changed;

        /// <summary>
        /// Returns null when the call may run now, otherwise the result to send back instead
        /// (awaiting approval, or denied).
        /// </summary>
        public static MCPResult Check(string tool, IDictionary<string, object> args)
        {
            if (SessionAllowedTools().Contains(tool)) return null;

            var argsJson = Canonical(args);
            var key = tool + "\n" + argsJson;
            var requests = LoadRequests();
            var existing = requests.Find(r => r.key == key);

            if (existing != null && existing.decision == Decision.Approved)
            {
                requests.Remove(existing);
                SaveRequests(requests);
                return null;
            }

            if (existing != null && existing.decision == Decision.Denied)
            {
                requests.Remove(existing);
                SaveRequests(requests);
                return MCPResult.Fail($"Denied by the user in Unity: '{tool}' was not run. Don't retry unless the user asks you to.");
            }

            if (existing == null)
            {
                requests.Add(new Request
                {
                    key = key,
                    tool = tool,
                    argsJson = argsJson,
                    decision = Decision.Pending,
                    requestedAt = DateTime.Now.ToString("HH:mm:ss")
                });
                SaveRequests(requests);
            }

            MCPApprovalWindow.ShowWindow();
            return MCPResult.Success(new
            {
                status = "awaiting_approval",
                tool,
                message = $"'{tool}' is in the danger tier and needs the user's approval in the Unity Editor " +
                          "(Window > Unity MCP > Approvals, now open). Ask the user to approve it there, then call " +
                          "this tool again with exactly the same arguments. It has NOT run yet."
            });
        }

        /// <summary>
        /// For a danger-tier ACTION inside an otherwise ordinary tool (manage_packages add/remove,
        /// execute_menu_item outside the menu allowlist): refused with a reason while the tier is
        /// off (the tool itself stays visible), approval-gated once it is on.
        /// </summary>
        public static MCPResult CheckDangerAction(string tool, string what, IDictionary<string, object> args)
        {
            if (!Groups.MCPToolGroupConfig.IsDangerTierEnabled())
                return MCPResult.Fail(
                    $"{what} is in the danger tier, which is off for this project. Ask the user to enable it in " +
                    "Window > Unity MCP > Tool Groups if they want this.");
            return Check(tool, args);
        }

        public static IReadOnlyList<Request> Pending => LoadRequests().Where(r => r.decision == Decision.Pending).ToList();

        public static void Resolve(string key, bool approve, bool allowToolForSession)
        {
            var requests = LoadRequests();
            var request = requests.Find(r => r.key == key);
            if (request == null) return;

            request.decision = approve ? Decision.Approved : Decision.Denied;
            SaveRequests(requests);

            if (approve && allowToolForSession)
            {
                var tools = SessionAllowedTools();
                tools.Add(request.tool);
                SessionState.SetString(SessionToolsKey, JsonConvert.SerializeObject(tools));
            }
            Changed?.Invoke();
        }

        /// <summary>Same tool + same arguments = same request, whatever order the agent sent the keys in.</summary>
        internal static string Canonical(IDictionary<string, object> args)
        {
            var token = JToken.FromObject(args ?? new Dictionary<string, object>());
            return Sort(token).ToString(Formatting.None);
        }

        private static JToken Sort(JToken token)
        {
            switch (token)
            {
                case JObject obj:
                    return new JObject(obj.Properties()
                        .OrderBy(p => p.Name, StringComparer.Ordinal)
                        .Select(p => new JProperty(p.Name, Sort(p.Value))));
                case JArray array:
                    return new JArray(array.Select(Sort));
                default:
                    return token;
            }
        }

        private static HashSet<string> SessionAllowedTools()
        {
            try
            {
                return JsonConvert.DeserializeObject<HashSet<string>>(SessionState.GetString(SessionToolsKey, "[]"))
                       ?? new HashSet<string>();
            }
            catch
            {
                return new HashSet<string>();
            }
        }

        private static List<Request> LoadRequests()
        {
            try
            {
                return JsonConvert.DeserializeObject<List<Request>>(SessionState.GetString(RequestsKey, "[]"))
                       ?? new List<Request>();
            }
            catch
            {
                return new List<Request>();
            }
        }

        private static void SaveRequests(List<Request> requests)
        {
            SessionState.SetString(RequestsKey, JsonConvert.SerializeObject(requests));
            Changed?.Invoke();
        }
    }
}
