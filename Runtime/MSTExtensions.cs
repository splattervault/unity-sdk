using System;
using System.Threading.Tasks;
using UnityEngine;

namespace SplatterVault.MST
{
    /// <summary>
    /// Extension methods for Master Server Toolkit integration
    /// </summary>
    public static class MSTExtensions
    {
        private const int PollIntervalMs = 5000;

        // A failed status request is retried this many times in a row before its error is thrown, so one
        // network blip does not end the wait but a lasting failure (bad key, missing session) surfaces fast.
        private const int MaxConsecutiveRequestFailures = 3;

        /// <summary>
        /// Get server information formatted for MST registration. <see cref="MSTServerInfo.ipAddress"/> is the
        /// server's IP (<see cref="GameSession.slaveIp"/>) and <see cref="MSTServerInfo.port"/> the port players join on
        /// (<see cref="GameSession.GetJoinPort"/>; null while it is not known yet).
        /// </summary>
        public static MSTServerInfo GetMSTServerInfo(this GameSession session)
        {
            return new MSTServerInfo
            {
                serverId = session.id.ToString(),
                serverCode = session.code,
                serverName = session.friendlyName ?? session.serverName,
                ipAddress = session.slaveIp,
                port = session.GetJoinPort(),
                maxPlayers = 10, // Configure based on your game
                gameType = session.gameType,
                mode = session.mode,
                region = session.region,
                status = session.status
            };
        }

        /// <summary>
        /// Poll a session (every 5 seconds) until it is "Active" (by default) or, with
        /// <paramref name="waitForGameReady"/>, until its game server is ready for players.
        ///
        /// With <paramref name="waitForGameReady"/> it returns the session when it is "Active" and the game has said
        /// it is ready (<see cref="GameSession.ready"/>). It also returns an "Active" session without that signal
        /// when the API does not report readiness, when the session's ready deadline
        /// (<see cref="GameSession.readyDeadlineAt"/>) has passed (a game with no ready signal configured never
        /// reports ready), or when <paramref name="maxWaitSeconds"/> runs out while it is Active.
        ///
        /// Throws <see cref="InvalidOperationException"/> as soon as the session is "Not Active" (it stopped or
        /// failed to start), <see cref="TimeoutException"/> when it never became Active within
        /// <paramref name="maxWaitSeconds"/>, and the request's own error when reading the session fails
        /// three times in a row.
        /// </summary>
        /// <param name="client">The client whose key may read the session.</param>
        /// <param name="sessionId">The session to watch.</param>
        /// <param name="maxWaitSeconds">How long to wait in total.</param>
        /// <param name="onStatusUpdate">Optional progress messages.</param>
        /// <param name="waitForGameReady">
        /// False (default): return as soon as the session is Active, as SDK 3.6 and earlier did.
        /// True: once Active, keep waiting for the game's ready signal as described above. Only use it for a game
        /// whose ready signal is configured for every mode it runs: otherwise the call waits until the ready
        /// deadline (about 5 minutes after the server is up).
        /// </param>
        public static async Task<GameSession> WaitForServerReady(
            this SplatterVaultClient client,
            int sessionId,
            int maxWaitSeconds = 300,
            Action<string> onStatusUpdate = null,
            bool waitForGameReady = false)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));

            DateTime giveUpAt = DateTime.UtcNow.AddSeconds(Math.Max(0, maxWaitSeconds));
            int consecutiveFailures = 0;
            GameSession last = null;

            while (true)
            {
                GameSession session = null;
                try
                {
                    session = await client.GetSessionAsync(sessionId);
                    consecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= MaxConsecutiveRequestFailures || DateTime.UtcNow >= giveUpAt)
                        throw;
                    onStatusUpdate?.Invoke($"Could not read session {sessionId} ({ex.Message}); retrying");
                }

                if (session != null)
                {
                    last = session;
                    onStatusUpdate?.Invoke($"Server status: {session.status}");

                    if (session.IsStopped())
                    {
                        string reason = string.IsNullOrEmpty(session.stopReason) ? "" : $" (stop reason: {session.stopReason})";
                        throw new InvalidOperationException($"Session {sessionId} stopped before it was ready{reason}");
                    }

                    if (session.IsActive())
                    {
                        if (!waitForGameReady || session.ready == null || session.ready == true)
                        {
                            onStatusUpdate?.Invoke("Server is ready!");
                            return session;
                        }

                        DateTime? readyBy = session.GetReadyDeadlineAt();
                        if (readyBy.HasValue && DateTime.UtcNow >= readyBy.Value)
                        {
                            onStatusUpdate?.Invoke("Server is up, but its game has not said it is ready by the ready deadline; returning it as is");
                            return session;
                        }

                        onStatusUpdate?.Invoke("Server is up; waiting for the game to say it is ready");
                    }
                }

                TimeSpan remaining = giveUpAt - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;

                await Task.Delay((int)Math.Min(PollIntervalMs, Math.Ceiling(remaining.TotalMilliseconds)));
            }

            if (last != null && last.IsActive())
            {
                onStatusUpdate?.Invoke($"Server is up, but its game did not say it is ready within {maxWaitSeconds} seconds; returning it as is");
                return last;
            }

            throw new TimeoutException($"Server did not become ready within {maxWaitSeconds} seconds");
        }

        /// <summary>
        /// Create a server and wait for it to be ready for MST registration
        /// </summary>
        public static async Task<MSTServerInfo> CreateAndWaitForMSTServer(
            this SplatterVaultClient client,
            CreateSessionRequest request,
            Action<string> onProgress = null)
        {
            onProgress?.Invoke("Creating server...");
            var session = await client.CreateCreditSessionAsync(request);

            onProgress?.Invoke($"Server created: {session.code}");
            onProgress?.Invoke("Waiting for server to start...");

            session = await client.WaitForServerReady(
                session.id,
                maxWaitSeconds: 300,
                onStatusUpdate: onProgress
            );

            onProgress?.Invoke("Server ready for registration!");
            return session.GetMSTServerInfo();
        }

        /// <summary>
        /// Check if a session is in a valid state for MST registration
        /// </summary>
        public static bool IsReadyForMST(this GameSession session)
        {
            return session.IsActive() &&
                   !string.IsNullOrEmpty(session.slaveIp) &&
                   !string.IsNullOrEmpty(session.code);
        }

        /// <summary>
        /// Get the connection string for MST clients: "ip:port", the server's IP (<see cref="GameSession.slaveIp"/>)
        /// and the port players join on (<see cref="GameSession.GetJoinPort"/>). Null until both are known.
        /// </summary>
        public static string GetConnectionString(this GameSession session)
        {
            if (string.IsNullOrEmpty(session.slaveIp))
                return null;

            int? port = session.GetJoinPort();
            if (!port.HasValue)
                return null;

            return $"{session.slaveIp}:{port.Value}";
        }
    }

    /// <summary>
    /// Server information structure for MST
    /// </summary>
    [Serializable]
    public class MSTServerInfo
    {
        public string serverId;
        public string serverCode;
        public string serverName;
        public string ipAddress;

        /// <summary>The port players join on (<see cref="GameSession.GetJoinPort"/>). Null while it is not known yet.</summary>
        public int? port;

        public int maxPlayers;
        public string gameType;
        public string mode;
        public string region;
        public string status;

        /// <summary>
        /// Get the full connection address, "ip:port". Null until both are known.
        /// </summary>
        public string GetConnectionAddress()
        {
            if (string.IsNullOrEmpty(ipAddress) || !port.HasValue)
                return null;

            return $"{ipAddress}:{port.Value}";
        }

        /// <summary>
        /// Convert to a dictionary for MST custom properties
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string> ToMSTProperties()
        {
            return new System.Collections.Generic.Dictionary<string, string>
            {
                { "serverCode", serverCode },
                { "gameType", gameType },
                { "mode", mode },
                { "region", region },
                { "splatterVaultId", serverId }
            };
        }
    }
}
