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
        private static string _cachedToken;

        /// <summary>Generates a new token for this bridge session and returns it for MCPSessionFile to persist.</summary>
        public static string EnsureToken()
        {
            _cachedToken = System.Guid.NewGuid().ToString("N") + System.Guid.NewGuid().ToString("N");
            return _cachedToken;
        }

        public static bool Validate(string presentedToken)
        {
            return !string.IsNullOrEmpty(presentedToken)
                   && !string.IsNullOrEmpty(_cachedToken)
                   && presentedToken == _cachedToken;
        }
    }
}
