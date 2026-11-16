using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SplatterVault.Core;

namespace SplatterVault.Players
{
    /// <summary>
    /// The studio store for the signed-in player (<c>/rest/store/*</c>): catalog, balances and items, history,
    /// buying with virtual currency, using items up, and the Meta Quest purchase check.
    /// <para><b>Idempotency.</b> Every change gets an Idempotency-Key when the player commits to it (a new
    /// <c>Guid</c>, or yours), and that same key on every retry, so the API applies it once. The key and the exact
    /// request are kept in secure storage until the API answers; if the game quits or loses its connection first,
    /// <see cref="ResumePendingAsync"/> sends them again (call it after sign-in). A repeat of a change that already
    /// happened answers <c>replayed: true</c> and is a success.</para>
    /// </summary>
    public sealed class SvStoreApi
    {
        /// <summary>The most of one item a single purchase buys.</summary>
        public const int MaxPurchaseQuantity = 100;

        private readonly SvPlayers _sv;
        private readonly SemaphoreSlim _pendingLock = new SemaphoreSlim(1, 1);

        /// <summary>Store changes being sent now, by player and key: one send per change at a time (rule 12).</summary>
        private readonly Dictionary<string, Flight> _inFlight = new Dictionary<string, Flight>(StringComparer.Ordinal);

        private sealed class Flight
        {
            public string Path;
            public string Body;
            public Task<SvStoreTxnResult> Task;
        }

        internal SvStoreApi(SvPlayers sv)
        {
            _sv = sv;
        }

        private string PendingKey => _sv.StoragePrefix + "pending_store_changes";

        /// <summary>Active currencies and items with prices, and what this player may buy or use up.</summary>
        public async Task<SvStoreCatalog> GetCatalogAsync(CancellationToken ct = default(CancellationToken))
        {
            var response = await _sv.Auth.SendAsPlayerAsync(Get("/store/catalog"), ct);
            return SvPipeline.Read<SvStoreCatalog>(response);
        }

        /// <summary>The player's balances and items.</summary>
        public async Task<SvStoreHoldings> GetMineAsync(CancellationToken ct = default(CancellationToken))
        {
            var response = await _sv.Auth.SendAsPlayerAsync(Get("/store/me"), ct);
            return SvPipeline.Read<SvStoreHoldings>(response);
        }

        /// <summary>The player's store history, newest first, 50 a page; pass the previous page's <c>Next</c> as <paramref name="before"/>.</summary>
        public async Task<SvStoreHistoryPage> GetHistoryAsync(string before = null, CancellationToken ct = default(CancellationToken))
        {
            var path = "/store/me/history" + (string.IsNullOrEmpty(before) ? "" : "?before=" + Uri.EscapeDataString(before));
            var response = await _sv.Auth.SendAsPlayerAsync(Get(path), ct);
            return SvPipeline.Read<SvStoreHistoryPage>(response);
        }

        /// <summary>
        /// Buys <paramref name="quantity"/> (1 to 100) of <paramref name="sku"/> with <paramref name="currency"/>.
        /// <paramref name="expectedPrice"/> is the unit price the player saw: a changed price is refused
        /// (<c>price_changed</c>). Pass <paramref name="idempotencyKey"/> to name the purchase yourself; by default a
        /// new key is made.
        /// </summary>
        public Task<SvStoreTxnResult> PurchaseAsync(string sku, string currency, long expectedPrice, int quantity = 1,
            string idempotencyKey = null, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(sku)) throw new ArgumentException("sku is required.", nameof(sku));
            if (string.IsNullOrEmpty(currency)) throw new ArgumentException("currency is required.", nameof(currency));
            if (expectedPrice < 0) throw new ArgumentOutOfRangeException(nameof(expectedPrice), "expectedPrice is 0 or more.");
            if (quantity < 1 || quantity > MaxPurchaseQuantity)
                throw new ArgumentOutOfRangeException(nameof(quantity), "quantity is 1 to " + MaxPurchaseQuantity + ".");
            var body = new JObject { ["sku"] = sku, ["currency"] = currency, ["quantity"] = quantity, ["expectedPrice"] = expectedPrice };
            return ChangeAsync("purchase", "/store/me/purchases", body, idempotencyKey, ct);
        }

        /// <summary>Uses up a held item: a one-of-a-kind copy, or <paramref name="quantity"/> of a stack.</summary>
        public Task<SvStoreTxnResult> ConsumeInstanceAsync(string instanceId, long? quantity = null, string idempotencyKey = null,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(instanceId)) throw new ArgumentException("instanceId is required.", nameof(instanceId));
            if (quantity.HasValue && quantity.Value < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
            var body = new JObject { ["instanceId"] = instanceId };
            if (quantity.HasValue) body["quantity"] = quantity.Value;
            return ChangeAsync("consume", "/store/me/consume", body, idempotencyKey, ct);
        }

        /// <summary>Uses up <paramref name="quantity"/> of <paramref name="sku"/>, oldest first.</summary>
        public Task<SvStoreTxnResult> ConsumeSkuAsync(string sku, long quantity, string idempotencyKey = null,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(sku)) throw new ArgumentException("sku is required.", nameof(sku));
            if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
            var body = new JObject { ["sku"] = sku, ["quantity"] = quantity };
            return ChangeAsync("consume", "/store/me/consume", body, idempotencyKey, ct);
        }

        /// <summary>
        /// Asks Meta for the player's Quest purchases and grants each mapped one once. Call it after a purchase and
        /// when the game starts; at most 6 a minute per player. <c>meta_not_linked</c>: the player has no Meta sign-in
        /// for the app.
        /// </summary>
        public async Task<SvMetaSyncResult> SyncMetaPurchasesAsync(string appId = null, CancellationToken ct = default(CancellationToken))
        {
            var body = new JObject();
            if (appId != null) body["appId"] = appId;
            // Not repeated by the SDK: each check asks Meta and spends one of the player's 6 a minute.
            var call = new SvCall { Method = "POST", Path = "/store/me/platform-purchases/meta", Body = body, Retry = SvRetry.Never };
            var response = await _sv.Auth.SendAsPlayerAsync(call, ct);
            return SvPipeline.Read<SvMetaSyncResult>(response);
        }

        /// <summary>Store changes of the signed-in player that have not been answered yet. Throws <c>storage_unreadable</c> when storage cannot tell.</summary>
        public async Task<IReadOnlyList<SvPendingStoreChange>> GetPendingAsync()
        {
            var player = _sv.Auth.PlayerId;
            var all = await LoadPendingAsync();
            return all.Where(p => player != null && p.PlayerId == player).ToList();
        }

        /// <summary>
        /// Sends the signed-in player's unanswered changes again, each with its own key and exact request. Returns
        /// the answered ones; a refused one (for example <c>insufficient_funds</c>) is dropped and reported through
        /// <paramref name="onRefused"/>; one that still gets no answer stays pending (the exception is thrown).
        /// <para>Each change is sent only as its own player: when another player signs in while this runs, it stops
        /// and leaves the rest pending for their player. A change finished meanwhile (by a concurrent call) is skipped.</para>
        /// <para>Every kept change is applied when it is resumed, including one the player has given up on: call this at
        /// a moment the player expects it (after sign-in, in the store), or <see cref="DiscardPendingAsync"/> it.</para>
        /// </summary>
        public async Task<IReadOnlyList<SvStoreTxnResult>> ResumePendingAsync(Action<SvPendingStoreChange, SvApiException> onRefused = null,
            CancellationToken ct = default(CancellationToken))
        {
            var results = new List<SvStoreTxnResult>();
            foreach (var change in await GetPendingAsync())
            {
                // Checked right before each send: the snapshot above may be stale (another player, a finished change).
                if (!string.Equals(_sv.Auth.PlayerId, change.PlayerId, StringComparison.Ordinal)) break;
                if (!await IsStillPendingAsync(change)) continue;
                try
                {
                    // A change already being sent is skipped, never joined (that send decides it; joining could wait
                    // forever when the game resumes from inside Reauthenticate while that send waits for it).
                    var result = await SendChangeAsync(change, true, ct, false);
                    if (result != null) results.Add(result);
                }
                catch (SvApiException e) when (e.Is(SvApiException.PlayerChangedCode))
                {
                    break;
                }
                catch (SvApiException e) when (!StaysPending(e))
                {
                    onRefused?.Invoke(change, e);
                }
            }
            return results;
        }

        /// <summary>The change is still in the kept list (same key, same player).</summary>
        private async Task<bool> IsStillPendingAsync(SvPendingStoreChange change)
        {
            await _pendingLock.WaitAsync();
            try
            {
                var list = await LoadPendingAsync();
                return list.Exists(p => SamePending(p, change.IdempotencyKey, change.PlayerId));
            }
            finally
            {
                _pendingLock.Release();
            }
        }

        private static bool SamePending(SvPendingStoreChange p, string key, string playerId)
        {
            return string.Equals(p.IdempotencyKey, key, StringComparison.Ordinal) && string.Equals(p.PlayerId, playerId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Forgets the signed-in player's unanswered change with this key (it may still have happened: check the
        /// history). When the kept list cannot be read (for example storage restored from a backup without its key),
        /// the whole list is deleted: nothing in it could be finished anyway. Throws <c>not_signed_in</c> when no player
        /// is signed in (use <see cref="DiscardAllPendingAsync"/> to forget every player's changes).
        /// </summary>
        public async Task DiscardPendingAsync(string idempotencyKey)
        {
            var player = _sv.Auth.PlayerId;
            if (player == null) throw new SvApiException(0, SvApiException.NotSignedInCode, null);
            await UpdatePendingAsync(list => list.RemoveAll(p => SamePending(p, idempotencyKey, player)), true);
        }

        /// <summary>Forgets every kept change of every player, without reading them (the way out of an unreadable list).</summary>
        public async Task DiscardAllPendingAsync()
        {
            await _pendingLock.WaitAsync();
            try
            {
                await DeletePendingListAsync();
            }
            finally
            {
                _pendingLock.Release();
            }
        }

        private async Task<SvStoreTxnResult> ChangeAsync(string kind, string path, JObject body, string idempotencyKey, CancellationToken ct)
        {
            var key = idempotencyKey ?? SvIdempotency.NewKey();
            if (!SvIdempotency.IsValidKey(key))
                throw new ArgumentException("An Idempotency-Key is 1 to 128 visible ASCII characters.", nameof(idempotencyKey));
            var player = _sv.Auth.PlayerId;
            if (player == null) throw new SvApiException(0, SvApiException.NotSignedInCode, null);

            var change = new SvPendingStoreChange
            {
                IdempotencyKey = key,
                Kind = kind,
                Path = path,
                Body = SvJson.Serialize(body),
                PlayerId = player,
                CreatedAt = _sv.Pipeline.Now.ToString("o", CultureInfo.InvariantCulture)
            };
            // Kept BEFORE the first send: a crash or a lost answer can then be finished with the same key. When storage
            // fails the change is still sent (with its key, so this call's own retries stay safe); it just cannot be
            // finished after a restart (StorageFailed was raised). A kept change with this key is never replaced: the
            // same request again (a "Retry" with its key) is sent as it was kept; another request is refused.
            SvPendingStoreChange keptAlready = null;
            var taken = false;
            var kept = await UpdatePendingAsync(list =>
            {
                var existing = list.Find(p => SamePending(p, key, player));
                if (existing == null) list.Add(change);
                else if (SameRequest(existing.Path, existing.Body, change)) keptAlready = existing;
                else taken = true;
            });
            if (taken) throw KeyTaken(change);
            // Already being sent: wait for that send (never a second one at once).
            return await SendChangeAsync(keptAlready ?? change, kept, ct, true);
        }

        private static string FlightKey(string playerId, string key) => playerId + "\n" + key;

        private static bool SameRequest(string path, string body, SvPendingStoreChange change)
        {
            return string.Equals(path, change.Path, StringComparison.Ordinal) && string.Equals(body, change.Body, StringComparison.Ordinal);
        }

        /// <summary>
        /// Another change (other arguments) already holds this key, kept or being sent: refused, nothing sent, nothing
        /// changed (the other change is left as it is).
        /// </summary>
        private static SvApiException KeyTaken(SvPendingStoreChange change)
        {
            return new SvApiException(0, SvApiException.ChangeInProgressCode,
                "Another change with this Idempotency-Key is kept or being sent; this one was not sent. Use a new key for a new change.")
            {
                IdempotencyKey = change.IdempotencyKey
            };
        }

        /// <summary>
        /// Sends one change with its key, once at a time. A send of the same change already in flight is joined (its answer
        /// is this one's) when <paramref name="join"/>, or skipped (null) otherwise (resume never joins). The network send
        /// runs to the end whatever happens to the callers: a caller's <paramref name="ct"/> cancels only its own wait
        /// (<see cref="SvChangeCanceledException"/>, with the key).
        /// </summary>
        private Task<SvStoreTxnResult> SendChangeAsync(SvPendingStoreChange change, bool kept, CancellationToken ct, bool join)
        {
            var flightKey = FlightKey(change.PlayerId, change.IdempotencyKey);
            TaskCompletionSource<SvStoreTxnResult> mine;
            lock (_inFlight)
            {
                if (_inFlight.TryGetValue(flightKey, out var flight))
                {
                    if (!join) return Task.FromResult<SvStoreTxnResult>(null);
                    if (!SameRequest(flight.Path, flight.Body, change)) return Task.FromException<SvStoreTxnResult>(KeyTaken(change));
                    return WaitAsync(flight.Task, change, true, ct);
                }
                mine = new TaskCompletionSource<SvStoreTxnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight[flightKey] = new Flight { Path = change.Path, Body = change.Body, Task = mine.Task };
            }
            _ = DriveAsync(change, kept, flightKey, mine);
            return WaitAsync(mine.Task, change, kept, ct);
        }

        /// <summary>One caller's wait for a send: cancelling it leaves the send (and the kept change) alone.</summary>
        private static async Task<SvStoreTxnResult> WaitAsync(Task<SvStoreTxnResult> send, SvPendingStoreChange change, bool kept,
            CancellationToken ct)
        {
            try
            {
                return await SvTasks.WithCancellation(send, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new SvChangeCanceledException(change.IdempotencyKey, kept, ct);
            }
        }

        private async Task DriveAsync(SvPendingStoreChange change, bool kept, string flightKey, TaskCompletionSource<SvStoreTxnResult> mine)
        {
            try
            {
                // Never cancelled by a caller: a send stopped half way leaves the outcome unknown for everyone waiting.
                var result = await SendChangeCoreAsync(change, kept, CancellationToken.None);
                Land(flightKey);
                mine.TrySetResult(result);
            }
            catch (Exception e)
            {
                Land(flightKey);
                mine.TrySetException(e);
            }
        }

        private void Land(string flightKey)
        {
            lock (_inFlight) _inFlight.Remove(flightKey);
        }

        /// <summary>Sends one change with its key; removes it from the kept list once answered.</summary>
        /// <param name="change">The change, with its key and exact body.</param>
        /// <param name="kept">The change is in the kept list (else there is nothing to remove, and no second storage error).</param>
        /// <param name="ct">Cancels waiting (the change may still happen).</param>
        private async Task<SvStoreTxnResult> SendChangeCoreAsync(SvPendingStoreChange change, bool kept, CancellationToken ct)
        {
            var call = new SvCall
            {
                Method = "POST",
                Path = change.Path,
                Body = SvJson.Parse(change.Body),
                // Safe: every repeat carries the same key, so the API applies the change once.
                Retry = SvRetry.Safe,
                Headers = new Dictionary<string, string> { ["Idempotency-Key"] = change.IdempotencyKey }
            };
            SvHttpResponse response;
            try
            {
                // Only ever with the token of the player who made the change (never charge another player).
                response = await _sv.Auth.SendAsPlayerAsync(call, ct, change.PlayerId);
            }
            catch (SvApiException e)
            {
                // The key goes with the error, so the game can send this exact change again (rule: once, same key), and
                // whether the change is still kept to be finished.
                e.IdempotencyKey = change.IdempotencyKey;
                e.IsPending = kept && StaysPending(e);
                // Answered with a refusal: nothing changed, nothing to finish.
                if (!StaysPending(e) && kept) await RemovePendingAsync(change.IdempotencyKey, change.PlayerId);
                throw;
            }
            var result = SvPipeline.Read<SvStoreTxnResult>(response);
            result.IdempotencyKey = change.IdempotencyKey;
            if (kept) await RemovePendingAsync(change.IdempotencyKey, change.PlayerId);
            return result;
        }

        /// <summary>
        /// No answer, a transient one, a sign-in problem or another player signed in (the change was not looked at),
        /// or an outcome the SDK cannot know: send it again later.
        /// </summary>
        private static bool StaysPending(SvApiException e)
        {
            return e.Status == 0 || e.IsTransient || e.Status == 401 || e.IsOutcomeUnknown;
        }

        private Task RemovePendingAsync(string key, string playerId)
        {
            return UpdatePendingAsync(list => list.RemoveAll(p => SamePending(p, key, playerId)));
        }

        private const string PendingItem = "pending_store_changes";

        /// <summary>
        /// The kept changes. Unreadable storage throws <c>storage_unreadable</c>: kept changes are then never
        /// overwritten by a shorter list.
        /// </summary>
        private async Task<List<SvPendingStoreChange>> LoadPendingAsync()
        {
            string json;
            try
            {
                json = await _sv.SecureStore.GetAsync(PendingKey);
            }
            catch (Exception e)
            {
                _sv.Auth.RaiseStorageFailed(PendingItem, SvStorageOperation.Read, e);
                throw SvAuth.Unreadable(PendingItem, e);
            }
            if (string.IsNullOrEmpty(json)) return new List<SvPendingStoreChange>();
            try
            {
                return SvJson.Deserialize<List<SvPendingStoreChange>>(json) ?? new List<SvPendingStoreChange>();
            }
            catch (Exception e)
            {
                _sv.Auth.RaiseStorageFailed(PendingItem, SvStorageOperation.Read, e);
                throw SvAuth.Unreadable(PendingItem, e);
            }
        }

        /// <summary>
        /// Changes the kept list. False (and nothing written) when it cannot be read or saved; with
        /// <paramref name="deleteIfUnreadable"/> (only for removals) an unreadable list is deleted instead.
        /// </summary>
        private async Task<bool> UpdatePendingAsync(Action<List<SvPendingStoreChange>> change, bool deleteIfUnreadable = false)
        {
            await _pendingLock.WaitAsync();
            try
            {
                List<SvPendingStoreChange> list;
                try
                {
                    list = await LoadPendingAsync();
                }
                catch (SvApiException)
                {
                    return deleteIfUnreadable && await DeletePendingListAsync();
                }
                change(list);
                try
                {
                    if (list.Count == 0) await _sv.SecureStore.DeleteAsync(PendingKey);
                    else await _sv.SecureStore.SetAsync(PendingKey, SvJson.Serialize(SvJson.FromObject(list)));
                    return true;
                }
                catch (Exception e)
                {
                    _sv.Auth.RaiseStorageFailed(PendingItem, SvStorageOperation.Write, e);
                    return false;
                }
            }
            finally
            {
                _pendingLock.Release();
            }
        }

        /// <summary>Drops every kept change of <paramref name="playerId"/> (the player was erased); an unreadable list is deleted.</summary>
        internal Task ForgetPendingForPlayerAsync(string playerId)
        {
            return UpdatePendingAsync(list => list.RemoveAll(p => p.PlayerId == playerId), true);
        }

        /// <summary>Deletes the kept list without reading it (call with the lock held).</summary>
        private async Task<bool> DeletePendingListAsync()
        {
            try
            {
                await _sv.SecureStore.DeleteAsync(PendingKey);
                return true;
            }
            catch (Exception e)
            {
                _sv.Auth.RaiseStorageFailed(PendingItem, SvStorageOperation.Delete, e);
                return false;
            }
        }

        private static SvCall Get(string path)
        {
            return new SvCall { Method = "GET", Path = path, Retry = SvRetry.Safe };
        }
    }
}
