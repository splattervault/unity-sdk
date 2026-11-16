// GAME CLIENT CODE (Steam, PC). No API key anywhere: a game build must never hold one.
// Signs the player in with a Steam Web API ticket (Steamworks.NET 20.2.0 or later).
//
// Compiled only when Steamworks.NET is in the project: with the scripting define STEAMWORKS_NET (which
// Steamworks.NET adds) or SV_STEAMWORKS (add it yourself), and not DISABLESTEAMWORKS.
//
// Set up first: Sign-in tab: the Steam app id and a publisher Web API key.
#if (SV_STEAMWORKS || STEAMWORKS_NET) && !DISABLESTEAMWORKS
using System;
using System.Threading.Tasks;
using SplatterVault.Core;
using SplatterVault.Players;
using Steamworks;
using UnityEngine;

public class SteamSignInExample : MonoBehaviour
{
    [Tooltip("Your game id, from the Sign-in tab of the game console")]
    [SerializeField] private int gameId = 123;

    private SvPlayers players;
    private Callback<GetTicketForWebApiResponse_t> onTicket;
    private HAuthTicket ticket = HAuthTicket.Invalid;
    private TaskCompletionSource<string> pendingTicket;

    private void Start()
    {
        players = new SvPlayers(gameId);
        // A Steam sign-in has no refresh token: when the player token runs out, get a new ticket.
        players.Auth.Reauthenticate = async (info, ct) =>
        {
            await SignInAsync();
            return players.Auth.IsSignedIn;
        };
    }

    /// <summary>Call once Steam is initialized (for example when SteamManager.Initialized is true).</summary>
    public async Task SignInAsync()
    {
        var hex = await GetTicketHexAsync();
        try
        {
            var result = await players.Auth.SignInWithSteamAsync(hex);
            Debug.Log("Signed in as SplatterVault player " + result.Player.Id);
        }
        catch (SvApiException e)
        {
            // publisher_banned, family_sharing_refused, proof_replayed (get a new ticket), provider_unavailable.
            Debug.LogWarning("SplatterVault sign-in failed: " + (e.ErrorCode ?? "HTTP " + e.Status));
        }
        finally
        {
            // A ticket works once: cancel it with Steam once SplatterVault has answered.
            SteamUser.CancelAuthTicket(ticket);
            ticket = HAuthTicket.Invalid;
        }
    }

    private Task<string> GetTicketHexAsync()
    {
        if (onTicket == null) onTicket = Callback<GetTicketForWebApiResponse_t>.Create(OnTicket);
        pendingTicket = new TaskCompletionSource<string>();
        ticket = SteamUser.GetAuthTicketForWebApi("splattervault"); // exactly this identity
        return pendingTicket.Task;
    }

    private void OnTicket(GetTicketForWebApiResponse_t response)
    {
        if (response.m_hAuthTicket != ticket || pendingTicket == null) return;
        if (response.m_eResult != EResult.k_EResultOK)
        {
            SteamUser.CancelAuthTicket(ticket);
            ticket = HAuthTicket.Invalid;
            pendingTicket.TrySetException(new InvalidOperationException("Steam ticket failed: " + response.m_eResult));
            return;
        }
        pendingTicket.TrySetResult(SvAuth.ToHex(response.m_rgubTicket, response.m_cubTicket));
    }
}
#endif
