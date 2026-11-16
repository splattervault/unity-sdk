using System;

namespace SplatterVault.Core
{
    /// <summary>
    /// Idempotency-Key values for store changes. Make one when the player commits to a change and send that same
    /// key on every retry of it: the API then applies the change once, and answers a repeat with the first result
    /// (<c>replayed: true</c>, HTTP 200). The same key with a different request is refused
    /// (<c>idempotency_conflict</c>).
    /// </summary>
    public static class SvIdempotency
    {
        /// <summary>A new key: 32 hex characters.</summary>
        public static string NewKey()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>A key the API accepts: 1 to 128 visible ASCII characters.</summary>
        public static bool IsValidKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 128) return false;
            foreach (var c in key)
                if (c < '!' || c > '~') return false;
            return true;
        }
    }
}
