// GAME CLIENT CODE (Meta Quest). No API key anywhere: a game build must never hold one.
// Signs the player in with their Meta account (Meta XR Platform SDK) and grants their Quest add-on purchases.
//
// Compiled only with the scripting define SV_META_PLATFORM (Project Settings > Player > Scripting Define
// Symbols), so this sample builds in projects without the Meta Platform SDK. Add the define once the SDK is in.
//
// Set up first: Sign-in tab: add the Quest app (app id and app secret). Store tab > Platform purchases: map
// each add-on SKU to an item. Meta requires a written service-provider agreement with SplatterVault.
#if SV_META_PLATFORM
using System;
using System.Threading.Tasks;
using Oculus.Platform;
using SplatterVault.Core;
using SplatterVault.Players;
using UnityEngine;

public class MetaQuestSignInExample : MonoBehaviour
{
    [Tooltip("Your game id, from the Sign-in tab of the game console")]
    [SerializeField] private int gameId = 123;

    [Tooltip("A Meta add-on SKU mapped to a store item")]
    [SerializeField] private string addOnSku = "coins_100";

    private SvPlayers players;

    private void Start()
    {
        players = new SvPlayers(gameId);
        // A Meta sign-in has no refresh token: when the player token runs out (1 hour), the SDK calls this once
        // with the waiting requests held, and sends them again with the new token.
        players.Auth.Reauthenticate = async (info, ct) =>
        {
            await SignInWithMetaAsync();
            return players.Auth.IsSignedIn;
        };
        Oculus.Platform.Core.AsyncInitialize().OnComplete(init =>
        {
            if (init.IsError)
            {
                UnityEngine.Debug.LogWarning("Meta Platform SDK did not start: " + init.GetError().Message);
                return;
            }
            _ = SignInAndSyncAsync();
        });
    }

    private async Task SignInAndSyncAsync()
    {
        try
        {
            await SignInWithMetaAsync();
            // Grants add-ons bought while the game was closed (and finishes any purchase left half-done).
            var sync = await players.Store.SyncMetaPurchasesAsync();
            UnityEngine.Debug.Log("Meta purchases: " + sync.Granted + " granted, " + sync.AlreadyGranted + " already granted");
        }
        catch (SvApiException e)
        {
            // not_entitled: the account does not own the app. proof_replayed: a nonce was used twice.
            UnityEngine.Debug.LogWarning("SplatterVault sign-in failed: " + (e.ErrorCode ?? "HTTP " + e.Status));
        }
    }

    /// <summary>A fresh user proof (nonce) for every sign-in: a nonce works once, also after provider_unavailable.</summary>
    private Task<SvSignInResult> SignInWithMetaAsync()
    {
        var done = new TaskCompletionSource<SvSignInResult>();
        Users.GetLoggedInUser().OnComplete(user =>
        {
            if (user.IsError)
            {
                done.TrySetException(new InvalidOperationException("Meta: no signed-in user"));
                return;
            }
            var userId = user.Data.ID; // a 64-bit id: never a float or double
            Users.GetUserProof().OnComplete(async proof =>
            {
                if (proof.IsError)
                {
                    done.TrySetException(new InvalidOperationException("Meta: no user proof"));
                    return;
                }
                try
                {
                    done.TrySetResult(await players.Auth.SignInWithMetaAsync(userId, proof.Data.Value));
                }
                catch (Exception e)
                {
                    done.TrySetException(e);
                }
            });
        });
        return done.Task;
    }

    /// <summary>Buys the add-on through Meta's checkout, then asks SplatterVault to grant it.</summary>
    public void BuyAddOn()
    {
        IAP.LaunchCheckoutFlow(addOnSku).OnComplete(async purchase =>
        {
            if (purchase.IsError) return; // cancelled or failed at Meta: nothing was paid
            try
            {
                var sync = await players.Store.SyncMetaPurchasesAsync();
                UnityEngine.Debug.Log("Granted " + sync.Granted + " purchase(s)");
            }
            catch (SvApiException e)
            {
                // Not lost: the next check (or the webhook) grants it. At most 6 checks a minute.
                UnityEngine.Debug.LogWarning("Purchase check failed: " + (e.ErrorCode ?? "HTTP " + e.Status));
            }
        });
    }
}
#endif
