using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace UnityMCP.Security
{
    /// <summary>
    /// The session token this Editor presents to Python MCP servers in its hello (protocol 2).
    /// Python checks it against Library/MCP/unity.json (MCPUnityInfoFile), which only the
    /// Editor holding this project's instance lock writes.
    ///
    /// Lifetime: one token per Editor launch. Kept in SessionState, which survives domain
    /// reloads but not an Editor restart, so a reload never invalidates it while a token
    /// that leaked dies when Unity closes.
    /// </summary>
    public static class MCPAuthHandshake
    {
        private const int TokenBytes = 32;
        private const string SessionKey = "UnityMCP.SessionToken";

        /// <summary>Returns this Editor session's token, creating it on first use.</summary>
        public static string EnsureToken()
        {
            var token = SessionState.GetString(SessionKey, "");
            if (!string.IsNullOrEmpty(token)) return token;

            token = NewToken();
            SessionState.SetString(SessionKey, token);
            return token;
        }

        // A cryptographically secure RNG, not Guid.NewGuid(): GUIDs are designed to be unique,
        // not unpredictable, and the token is the whole auth story.
        internal static string NewToken()
        {
            var bytes = new byte[TokenBytes];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
