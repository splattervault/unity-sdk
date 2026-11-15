// SERVER-SIDE ONLY: for your Master Server Toolkit (MST) master server.
// This sample starts and stops servers with your SplatterVault API key, so it is compiled only into the
// Unity Editor and dedicated server builds (UNITY_SERVER). Never ship the key in a game client build:
// anyone who pulls it out of the build can start and stop your servers and spend your (or your
// organization's) credits. Game clients will use player sign-in instead (coming in SDK 4.0).
#if UNITY_SERVER || UNITY_EDITOR
using System;
using System.Threading.Tasks;
using UnityEngine;
using SplatterVault;
using SplatterVault.MST;

/// <summary>
/// Minimal MST spawner backed by SplatterVault. Put it on a GameObject in your MST master server.
///
/// How it fits MST: the game server that SplatterVault starts registers its own room with your master,
/// exactly as it would on any other machine. This component only replaces "start a server process":
/// call <see cref="SpawnAsync"/> from your master's spawn handler. It hands the master's public address to
/// the game server as launch arguments. It uses no MST types, so it compiles with or without MST installed.
///
/// The launch-argument flags must be flags your game declares in its SplatterVault game config (flags it
/// does not declare are ignored). The defaults are MST's standard command-line names.
///
/// The API key is read from the SV_API_KEY environment variable of the machine running the
/// master server (or the Editor); it is never stored in a serialized field, scene or prefab.
/// </summary>
public class SplatterVaultMSTSpawner : MonoBehaviour
{
    private const string ApiKeyEnvVar = "SV_API_KEY";

    [Header("SplatterVault")]
    [Tooltip("Game config key from your SplatterVault dashboard")]
    [SerializeField] private string gameKey = "your_game_key_here";
    [SerializeField] private Region region = Region.NYC3;

    [Tooltip("Optional: claim a warm server from this server pool first (0 = always start a new server)")]
    [SerializeField] private int poolId = 0;

    [Header("MST master address handed to the game server")]
    [SerializeField] private string masterIpFlag = "-mstMasterIp";
    [SerializeField] private string masterPortFlag = "-mstMasterPort";
    [Tooltip("Public IP or host name of this master server, reachable from the internet")]
    [SerializeField] private string masterPublicAddress = "";
    [SerializeField] private int masterPort = 5000;

    private SplatterVaultClient client;

    /// <summary>
    /// Start a game server for one room. Returns its session once the session is Active: the server is
    /// up, but the game may still be loading (it registers its room with the master itself when it is
    /// ready). Keep the session to stop the server later. Throws if the session stops before it is
    /// Active, or is not Active within 5 minutes.
    /// </summary>
    public async Task<GameSession> SpawnAsync(string roomName)
    {
        SplatterVaultClient sv = await GetClientAsync();
        int sessionId = 0;

        if (poolId > 0)
        {
            var claim = new ClaimPoolServerRequest { friendlyName = roomName };
            AddMasterAddress(claim.AddCustomVariable);
            try
            {
                PoolClaimResponse claimed = await sv.ClaimPoolServerAsync(poolId, claim);
                sessionId = claimed.sessionId;
            }
            catch (Exception ex)
            {
                // Usually 409: no warm server is free right now. Start a new one instead.
                Debug.LogWarning($"[SplatterVault] Pool {poolId} claim failed ({ex.Message}); starting a new server");
            }
        }

        if (sessionId == 0)
        {
            var request = new CreateSessionRequest { gameKey = gameKey, friendlyName = roomName };
            request.SetRegion(region);
            AddMasterAddress(request.AddCustomVariable);
            GameSession created = await sv.CreateCreditSessionAsync(request);
            sessionId = created.id;
        }

        // Throws when the session stops before it is Active, or is not Active within 5 minutes.
        return await sv.WaitForServerReady(sessionId, onStatusUpdate: status => Debug.Log($"[SplatterVault] {status}"));
    }

    /// <summary>
    /// Stop a server started by <see cref="SpawnAsync"/>. A pool server goes back to its pool.
    /// </summary>
    public async Task StopAsync(GameSession session)
    {
        SplatterVaultClient sv = await GetClientAsync();
        await sv.StopSessionAsync(session);
    }

    private async Task<SplatterVaultClient> GetClientAsync()
    {
        if (client != null)
            return client;

        string apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvVar);
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException($"Set the {ApiKeyEnvVar} environment variable (sv_... or sv_org_...) on the master server.");

        client = await SplatterVaultClient.CreateAsync(apiKey);
        return client;
    }

    private void AddMasterAddress(Action<string, object> addLaunchArg)
    {
        if (string.IsNullOrEmpty(masterPublicAddress))
            throw new InvalidOperationException("Set masterPublicAddress to the public address of this master server.");

        addLaunchArg(masterIpFlag, masterPublicAddress);
        addLaunchArg(masterPortFlag, masterPort.ToString());
    }
}
#endif
