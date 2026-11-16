// GAME CLIENT CODE. No API key anywhere: a game build must never hold one.
// Shows the SplatterVault store: the catalog, the player's balances and items, buying with virtual currency,
// using an item up, and the history.
//
// Set up first: game console > Store tab: switch the store on, and "player purchases" / "player consumption"
// on for the store and for the items; Sign-in tab: switch on "Guest accounts".
//
// Never let the game client grant itself anything: rewards come from your backend (organization API key, on
// your server). The client only buys with currency it holds and uses up items it holds.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SplatterVault.Core;
using SplatterVault.Players;
using UnityEngine;

public class StoreExample : MonoBehaviour
{
    [Tooltip("Your game id, from the Sign-in tab of the game console")]
    [SerializeField] private int gameId = 123;

    [Tooltip("The currency the player pays with")]
    [SerializeField] private string currency = "GOLD";

    private SvPlayers players;
    private SvStoreCatalog catalog;
    private SvStoreHoldings mine;
    private readonly List<SvHistoryEntry> history = new List<SvHistoryEntry>();
    private string historyNext;
    private string status = "Starting...";
    private bool busy;
    private bool hasPending; // a change is kept unfinished (e.IsPending): offer "Finish", never "Buy" again

    private async void Start()
    {
        players = new SvPlayers(gameId);
        await Run("Loading the store", async () =>
        {
            if (!await players.Auth.RestoreAsync())
            {
                // Nothing restored: this sample plays as the device's guest. A real game shows its sign-in screen here,
                // above all when GetLastSignInMethodAsync() names a platform (the player expects that account, not a guest).
                await players.Auth.SignInAsGuestAsync();
            }
            // Finish changes the game quit (or lost its connection) in the middle of. Each is sent with the key it
            // was first sent with, so the API applies it once; replayed: true means it had already happened.
            var finished = await players.Store.ResumePendingAsync(
                (change, error) => Debug.Log("A pending " + change.Kind + " was refused: " + error.ErrorCode));
            if (finished.Count > 0) Debug.Log("Finished " + finished.Count + " pending store change(s)");
            hasPending = (await players.Store.GetPendingAsync()).Count > 0;
            await RefreshAsync();
            status = "Ready";
        });
    }

    private async Task RefreshAsync()
    {
        catalog = await players.Store.GetCatalogAsync();
        mine = await players.Store.GetMineAsync();
    }

    private Task BuyAsync(SvCatalogItem item)
    {
        return Run("Buying " + item.Name, async () =>
        {
            var price = item.PriceIn(currency);
            if (price == null)
            {
                status = item.Name + " has no " + currency + " price";
                return;
            }
            try
            {
                // The SDK makes the Idempotency-Key here, when the player commits, and reuses it on every retry.
                // expectedPrice is the price the player saw: a changed price is refused, never charged.
                var result = await players.Store.PurchaseAsync(item.Sku, currency, price.Value);
                status = (result.Replayed ? "Already bought: " : "Bought: ") + item.Name;
                mine = await players.Store.GetMineAsync(); // trust the API's balances, not your own arithmetic
            }
            catch (SvApiException e) when (e.ErrorCode == "price_changed")
            {
                await RefreshAsync();
                status = "The price of " + item.Name + " changed: check it and buy again";
            }
            catch (SvApiException e) when (e.IsPending)
            {
                // Kept to be finished: no answer, or an answer like 429/503 that a later resume will still apply.
                // The "Finish" button sends exactly that change again (e.IdempotencyKey) and is never charged twice.
                // Buying again would be a NEW purchase, so Buy stays off until it is finished.
                hasPending = true;
                status = "Not finished: " + item.Name + ". Press \"Finish unanswered purchases\" (never Buy again)";
            }
        });
    }

    /// <summary>The "Retry" of a purchase that got no answer: resume, never PurchaseAsync again without the key.</summary>
    private Task FinishPendingAsync()
    {
        return Run("Finishing", async () =>
        {
            var finished = await players.Store.ResumePendingAsync(
                (change, error) => Debug.Log("A pending " + change.Kind + " was refused: " + error.ErrorCode));
            hasPending = (await players.Store.GetPendingAsync()).Count > 0;
            mine = await players.Store.GetMineAsync();
            status = hasPending ? "Still no answer: try again later" : "Finished " + finished.Count + " change(s)";
        });
    }

    private Task UseAsync(SvInventoryItem held)
    {
        return Run("Using " + held.Sku, async () =>
        {
            var definition = catalog?.Find(held.Sku);
            // A stack: use one of it. A one-of-a-kind copy: name the copy only.
            long? quantity = definition != null && definition.Stackable ? 1 : (long?)null;
            await players.Store.ConsumeInstanceAsync(held.InstanceId, quantity);
            mine = await players.Store.GetMineAsync();
            status = "Used " + held.Sku;
        });
    }

    private Task LoadHistoryAsync()
    {
        return Run("Loading history", async () =>
        {
            var page = await players.Store.GetHistoryAsync(historyNext);
            history.AddRange(page.Txns);
            historyNext = page.Next; // null: no older entries
            status = history.Count + " entries";
        });
    }

    private async Task Run(string what, Func<Task> action)
    {
        busy = true;
        status = what + "...";
        try
        {
            await action();
        }
        catch (SvApiException e)
        {
            if (e.IsPending) hasPending = true; // a purchase or a use that is kept unfinished
            status = Describe(e);
        }
        finally
        {
            busy = false;
        }
    }

    // Act on ErrorCode, never on the message text.
    private static string Describe(SvApiException e)
    {
        switch (e.ErrorCode)
        {
            case "insufficient_funds": return "Not enough currency.";
            case "insufficient_items": return "You don't have enough of that.";
            case "already_owned": return "You already own as many as you can.";
            case "currency_cap": return "That would go over the currency's cap.";
            case "inventory_full": return "Your inventory is full.";
            case "purchases_disabled": return "That can't be bought in the game.";
            case "consume_disabled": return "That can't be used up in the game.";
            case "store_not_enabled": return "The store is off for this game.";
            case "rate_limited": return "Too many requests. Wait a minute.";
        }
        if (e.IsPending)
            return "Not finished yet. The change is kept and finished with \"Finish unanswered purchases\" (ResumePendingAsync); it is never applied twice.";
        if (e.IsTransient)
            return "No answer yet. Try again in a moment.";
        return "Something went wrong (" + (e.ErrorCode ?? "HTTP " + e.Status) + ").";
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(20, 20, 460, Screen.height - 40));
        GUILayout.Label(status);
        var ready = !busy && players != null && players.Auth.IsSignedIn;
        // While a change is kept unfinished, only "Finish" is offered: buying or using again would be a second change.
        GUI.enabled = ready && !hasPending;
        if (mine != null)
        {
            GUILayout.Label(currency + ": " + mine.BalanceOf(currency));
            foreach (var held in mine.Items)
                if (GUILayout.Button("Use " + held.Sku + " (x" + held.Quantity + ")")) _ = UseAsync(held);
        }
        GUI.enabled = ready;
        if (hasPending && GUILayout.Button("Finish unanswered purchases")) _ = FinishPendingAsync();
        GUI.enabled = ready && !hasPending;
        GUILayout.Space(10);
        if (catalog != null)
            foreach (var item in catalog.Items)
            {
                if (!item.Purchasable) continue;
                var price = item.PriceIn(currency);
                if (price != null && GUILayout.Button("Buy " + item.Name + " for " + price.Value + " " + currency)) _ = BuyAsync(item);
            }
        GUILayout.Space(10);
        GUI.enabled = ready;
        if ((history.Count == 0 || historyNext != null) && GUILayout.Button(history.Count == 0 ? "Show history" : "Older entries"))
            _ = LoadHistoryAsync();
        foreach (var entry in history) GUILayout.Label(entry.CreatedAt + "  " + entry.Kind);
        GUI.enabled = true;
        GUILayout.EndArea();
    }
}
