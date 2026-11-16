using System;

namespace SplatterVault.Core
{
    public enum SvLogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    /// <summary>The SDK's log lines. They never contain a token, a secret, a proof, a password or a value.</summary>
    public delegate void SvLogger(SvLogLevel level, string message);

    internal static class SvLog
    {
        internal static void Write(SvLogger logger, SvLogLevel level, string message)
        {
            if (logger == null) return;
            try
            {
                logger(level, "[SplatterVault] " + message);
            }
            catch (Exception)
            {
                // A logger that throws never breaks a request.
            }
        }
    }
}
