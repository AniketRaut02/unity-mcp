using System.Security.Cryptography;
using System.Text;

namespace UnityMCP.Security
{
    /// <summary>
    /// Generates a fresh random session token every time the bridge (re)starts. The token
    /// itself is the entire auth story: no token (or a stale/wrong one) on the handshake
    /// message means the connection is rejected before any tool ever runs.
    ///
    /// File persistence lives in MCPSessionFile, not here — this class only owns the
    /// in-memory token lifecycle (generate once per bridge start, validate on handshake).
    /// </summary>
    public static class MCPAuthHandshake
    {
        private const int TokenBytes = 32;

        private static string _cachedToken;

        /// <summary>Generates a new token for this bridge session and returns it for MCPSessionFile to persist.</summary>
        public static string EnsureToken()
        {
            // A cryptographically secure RNG, not Guid.NewGuid() -- GUIDs are designed
            // to be unique, not unpredictable, and are not a documented CSPRNG contract
            // to build an auth token's unguessability on, even though .NET's actual
            // implementation happens to source its randomness reasonably well today.
            var bytes = new byte[TokenBytes];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));

            _cachedToken = sb.ToString();
            return _cachedToken;
        }

        public static bool Validate(string presentedToken)
        {
            if (string.IsNullOrEmpty(presentedToken) || string.IsNullOrEmpty(_cachedToken))
                return false;

            // Constant-time comparison so a handshake attempt can't use response-timing
            // differences to narrow down the token byte by byte. The blast radius of
            // that attack is already limited (loopback-only socket, matching PID/port
            // per project), but the token is the entire auth story here, so it costs
            // nothing to compare it the way a secret is supposed to be compared.
            return FixedTimeEquals(presentedToken, _cachedToken);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }
    }
}
