using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SplatterVault.Core;

// Answer models of the player routes (player sign-in and accounts, player data, the store; see the API reference).
// Dates are ISO 8601 strings, exactly as the API sends them; the *Utc helpers parse them. Counts, versions, balances
// and quantities are long (at most 2^53 - 1).
namespace SplatterVault.Players
{
    /// <summary>The ways a player signs in (the token's <c>amr</c>, and <c>identities[].provider</c>).</summary>
    public static class SvProviders
    {
        public const string Xsolla = "xsolla";
        public const string Meta = "meta";
        public const string Steam = "steam";
        public const string PlayFab = "playfab";
        public const string Supabase = "supabase";
        /// <summary>Your backend vouched for the player (organization API key, on your server only).</summary>
        public const string Studio = "studio";
        /// <summary>A SplatterVault email account.</summary>
        public const string Email = "email";
        /// <summary>A guest on this device.</summary>
        public const string Device = "device";
    }

    internal static class SvDates
    {
        internal static DateTimeOffset? Parse(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                ? at
                : (DateTimeOffset?)null;
        }
    }

    // ─── sign-in ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A way the player can sign in, and when it was linked.</summary>
    [Preserve]
    public sealed class SvIdentity
    {
        [JsonProperty("provider")] public string Provider { get; set; }
        [JsonProperty("linkedAt")] public string LinkedAt { get; set; }
    }

    /// <summary>The player of a sign-in answer.</summary>
    [Preserve]
    public sealed class SvSignedInPlayer
    {
        /// <summary>The SplatterVault player id (uuid): the token's <c>sub</c>. It never changes.</summary>
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("displayName")] public string DisplayName { get; set; }
        [JsonProperty("createdAt")] public string CreatedAt { get; set; }
        /// <summary>This sign-in made the player.</summary>
        [JsonProperty("newPlayer")] public bool NewPlayer { get; set; }
    }

    /// <summary>
    /// What a sign-in answers (the player token itself stays in <see cref="SvAuth.PlayerToken"/>; refresh tokens
    /// and device secrets go straight to secure storage and are never handed out).
    /// </summary>
    [Preserve]
    public sealed class SvSignInResult
    {
        /// <summary>How the player signed in (<see cref="SvProviders"/>).</summary>
        public string Method { get; set; }
        /// <summary>When the player token runs out (ISO 8601).</summary>
        public string ExpiresAt { get; set; }
        public SvSignedInPlayer Player { get; set; }
        public List<SvIdentity> Identities { get; set; } = new List<SvIdentity>();
        /// <summary>
        /// The SDK can renew this sign-in by itself, also after a restart: its refresh token, or this device's guest
        /// secret, is saved in secure storage. False when there is none, or saving it failed (see
        /// <see cref="SavedToDevice"/>).
        /// </summary>
        public bool Renewable { get; set; }

        /// <summary>
        /// False when this sign-in returned a refresh token or a new guest secret and secure storage could not save it
        /// (<see cref="SvAuth.StorageFailed"/> was raised). The secret is kept in memory for this session only: a new
        /// guest is then lost when the game quits unless the player adds an email, or
        /// <see cref="SvAuth.RetrySavingSecretsAsync"/> succeeds.
        /// </summary>
        public bool SavedToDevice { get; set; } = true;

        /// <summary>The API's expiry, on the API's clock (compare with <see cref="SvAuth.TokenExpiresAtUtc"/>, on this device's clock).</summary>
        public DateTimeOffset? ExpiresAtUtc => SvDates.Parse(ExpiresAt);
        public bool IsNewPlayer => Player != null && Player.NewPlayer;
    }

    /// <summary>The player of <c>GET /player-auth/me</c>.</summary>
    [Preserve]
    public sealed class SvPlayerInfo
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("displayName")] public string DisplayName { get; set; }
        [JsonProperty("createdAt")] public string CreatedAt { get; set; }
        [JsonProperty("lastSignedInAt")] public string LastSignedInAt { get; set; }
    }

    [Preserve]
    public sealed class SvGameRef
    {
        /// <summary>The game's Game Key when the token was issued.</summary>
        [JsonProperty("gameKey")] public string GameKey { get; set; }
    }

    /// <summary>The signed-in player (<c>GET /player-auth/me</c>, and the answer of link and unlink).</summary>
    [Preserve]
    public sealed class SvPlayerProfile
    {
        [JsonProperty("player")] public SvPlayerInfo Player { get; set; }
        [JsonProperty("game")] public SvGameRef Game { get; set; }
        [JsonProperty("identities")] public List<SvIdentity> Identities { get; set; } = new List<SvIdentity>();

        public bool HasIdentity(string provider)
        {
            if (Identities == null) return false;
            foreach (var i in Identities)
                if (i != null && i.Provider == provider) return true;
            return false;
        }
    }

    /// <summary>An email code was asked for (always answered, whether or not a code was sent).</summary>
    [Preserve]
    public sealed class SvCodeSent
    {
        /// <summary>How long a code lasts (600).</summary>
        [JsonProperty("expiresInSeconds")] public int ExpiresInSeconds { get; set; }
        /// <summary>Wait this long before asking again (60).</summary>
        [JsonProperty("resendAfterSeconds")] public int ResendAfterSeconds { get; set; }
    }

    /// <summary>Why the game must sign the player in again.</summary>
    public enum SvSignInRequiredReason
    {
        /// <summary>The player token ran out and the SDK cannot renew it (no refresh token or guest secret).</summary>
        TokenExpired,
        /// <summary>The token is not valid any more (the game changed owner, or it is not one of ours).</summary>
        TokenInvalid,
        /// <summary>The player was erased.</summary>
        PlayerDeleted,
        /// <summary>The email sign-in ended (password changed elsewhere, signed out, expired, game moved).</summary>
        RefreshInvalid,
        /// <summary>No guest has this device's secret any more (unlinked or erased).</summary>
        DeviceSecretRejected
    }

    public enum SvStorageOperation
    {
        Read,
        Write,
        Delete
    }

    /// <summary>Secure storage failed (raised by <see cref="SvAuth.StorageFailed"/>). It never carries a value.</summary>
    public sealed class SvStorageFailure
    {
        public SvStorageFailure(string item, SvStorageOperation operation, Exception error)
        {
            Item = item;
            Operation = operation;
            Error = error;
        }

        /// <summary><c>refresh_token</c>, <c>guest_secret</c>, <c>last_sign_in</c> or <c>pending_store_changes</c>.</summary>
        public string Item { get; }
        public SvStorageOperation Operation { get; }
        public Exception Error { get; }
    }

    /// <summary>Raised when the player must sign in again with a fresh proof.</summary>
    public sealed class SvSignInRequired
    {
        public SvSignInRequired(SvSignInRequiredReason reason, string method, string errorCode)
        {
            Reason = reason;
            Method = method;
            ErrorCode = errorCode;
        }

        public SvSignInRequiredReason Reason { get; }
        /// <summary>How the player had signed in (<see cref="SvProviders"/>), or null.</summary>
        public string Method { get; }
        /// <summary>The API's code that ended the sign-in.</summary>
        public string ErrorCode { get; }
    }

    // Internal: the full answer, secrets included (never handed to the game).
    [Preserve]
    internal sealed class SignInAnswer
    {
        [JsonProperty("token")] public string Token { get; set; }
        [JsonProperty("expiresAt")] public string ExpiresAt { get; set; }
        [JsonProperty("player")] public SvSignedInPlayer Player { get; set; }
        [JsonProperty("identities")] public List<SvIdentity> Identities { get; set; }
        [JsonProperty("refreshToken")] public string RefreshToken { get; set; }
        [JsonProperty("refreshExpiresAt")] public string RefreshExpiresAt { get; set; }
        [JsonProperty("deviceSecret")] public string DeviceSecret { get; set; }
    }

    [Preserve]
    internal sealed class ProfileAnswer
    {
        [JsonProperty("player")] public SvPlayerInfo Player { get; set; }
        [JsonProperty("game")] public SvGameRef Game { get; set; }
        [JsonProperty("identities")] public List<SvIdentity> Identities { get; set; }
        [JsonProperty("refreshToken")] public string RefreshToken { get; set; }
        [JsonProperty("refreshExpiresAt")] public string RefreshExpiresAt { get; set; }
    }

    [Preserve]
    internal sealed class RefreshGrantAnswer
    {
        [JsonProperty("refreshToken")] public string RefreshToken { get; set; }
        [JsonProperty("refreshExpiresAt")] public string RefreshExpiresAt { get; set; }
    }

    // ─── player data ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One stored key: its JSON value, its version and when it last changed.</summary>
    [Preserve]
    public sealed class SvDataEntry
    {
        /// <summary>The value, or null when the read asked for keys only.</summary>
        [JsonProperty("value")] public JToken Value { get; set; }
        /// <summary>Only ever goes up when the key changes. Compare versions; don't count them.</summary>
        [JsonProperty("version")] public long Version { get; set; }
        [JsonProperty("updatedAt")] public string UpdatedAt { get; set; }

        /// <summary>The value as <typeparamref name="T"/> (default when there is none).</summary>
        public T Get<T>() => SvJson.ToObject<T>(Value);

        public DateTimeOffset? UpdatedAtUtc => SvDates.Parse(UpdatedAt);
    }

    /// <summary>The keys of one store.</summary>
    public sealed class SvDataStore
    {
        public SvDataStore(Dictionary<string, SvDataEntry> entries = null)
        {
            Entries = entries ?? new Dictionary<string, SvDataEntry>(StringComparer.Ordinal);
        }

        public Dictionary<string, SvDataEntry> Entries { get; }

        public bool Contains(string key) => key != null && Entries.ContainsKey(key);

        /// <summary>The entry, or null.</summary>
        public SvDataEntry this[string key] => key != null && Entries.TryGetValue(key, out var e) ? e : null;

        /// <summary>The value of <paramref name="key"/> as <typeparamref name="T"/>, or <paramref name="fallback"/>.</summary>
        public T Get<T>(string key, T fallback = default(T))
        {
            var e = this[key];
            return e == null || e.Value == null || e.Value.Type == JTokenType.Null ? fallback : e.Get<T>();
        }

        /// <summary>The version of <paramref name="key"/>, or 0 when it does not exist (0 is what <c>ifVersion</c> takes for "must not exist").</summary>
        public long VersionOf(string key)
        {
            var e = this[key];
            return e == null ? 0 : e.Version;
        }
    }

    /// <summary>The signed-in player's data (<c>GET /player-data/me</c>): the <c>player</c> and <c>readonly</c> stores.</summary>
    public sealed class SvPlayerData
    {
        public SvPlayerData(Dictionary<string, SvDataStore> stores)
        {
            Stores = stores ?? new Dictionary<string, SvDataStore>(StringComparer.Ordinal);
        }

        public Dictionary<string, SvDataStore> Stores { get; }

        /// <summary>What the player writes (untrusted: never keep currency, XP or unlocks here).</summary>
        public SvDataStore Player => StoreOf("player");

        /// <summary>What only your backend writes; the player reads it.</summary>
        public SvDataStore Readonly => StoreOf("readonly");

        public SvDataStore StoreOf(string name) => name != null && Stores.TryGetValue(name, out var s) ? s : new SvDataStore();
    }

    /// <summary>A game's public data, with the ETag it came with.</summary>
    public sealed class SvGameData
    {
        public SvGameData(SvDataStore data, string etag, bool notModified)
        {
            Data = data ?? new SvDataStore();
            ETag = etag;
            NotModified = notModified;
        }

        public SvDataStore Data { get; }
        public string ETag { get; }
        /// <summary>The API answered 304: nothing changed since the copy the SDK kept, which is returned.</summary>
        public bool NotModified { get; }
    }

    /// <summary>One op's result, in the order of the ops.</summary>
    [Preserve]
    public sealed class SvDataOpResult
    {
        [JsonProperty("store")] public string Store { get; set; }
        [JsonProperty("key")] public string Key { get; set; }
        /// <summary>The key's version after the op; null when it no longer exists (a delete).</summary>
        [JsonProperty("version")] public long? Version { get; set; }
        /// <summary>An <c>inc</c>'s stored value after it; null for other ops.</summary>
        [JsonProperty("value")] public JToken Value { get; set; }
    }

    [Preserve]
    public sealed class SvDataWriteResult
    {
        [JsonProperty("results")] public List<SvDataOpResult> Results { get; set; } = new List<SvDataOpResult>();
    }

    /// <summary>One current version of a <c>version_conflict</c> (<c>SvApiException.DetailsAs&lt;List&lt;SvVersionInfo&gt;&gt;()</c>).</summary>
    [Preserve]
    public sealed class SvVersionInfo
    {
        [JsonProperty("store")] public string Store { get; set; }
        [JsonProperty("key")] public string Key { get; set; }
        [JsonProperty("version")] public long? Version { get; set; }
    }

    [Preserve]
    internal sealed class PlayerDataAnswer
    {
        [JsonProperty("data")] public Dictionary<string, Dictionary<string, SvDataEntry>> Data { get; set; }
    }

    [Preserve]
    internal sealed class GameDataAnswer
    {
        [JsonProperty("data")] public Dictionary<string, SvDataEntry> Data { get; set; }
    }

    // ─── store ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Preserve]
    public sealed class SvStoreSettings
    {
        /// <summary>Players may buy with virtual currency from the game (the item's own switch applies too).</summary>
        [JsonProperty("playerPurchases")] public bool PlayerPurchases { get; set; }
        /// <summary>Players may use up items from the game (the item's own switch applies too).</summary>
        [JsonProperty("playerConsume")] public bool PlayerConsume { get; set; }
    }

    [Preserve]
    public sealed class SvCurrency
    {
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary>The most one player may hold, or null.</summary>
        [JsonProperty("cap")] public long? Cap { get; set; }
    }

    [Preserve]
    public sealed class SvPrice
    {
        [JsonProperty("currency")] public string Currency { get; set; }
        [JsonProperty("amount")] public long Amount { get; set; }
    }

    /// <summary>A bundle line: exactly one of <see cref="Sku"/> and <see cref="Currency"/>.</summary>
    [Preserve]
    public sealed class SvBundleLine
    {
        [JsonProperty("sku")] public string Sku { get; set; }
        [JsonProperty("currency")] public string Currency { get; set; }
        [JsonProperty("quantity")] public long Quantity { get; set; }
    }

    [Preserve]
    public sealed class SvCatalogItem
    {
        [JsonProperty("sku")] public string Sku { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary><c>item</c> or <c>bundle</c>.</summary>
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("stackable")] public bool Stackable { get; set; }
        [JsonProperty("maxOwned")] public long? MaxOwned { get; set; }
        [JsonProperty("prices")] public List<SvPrice> Prices { get; set; } = new List<SvPrice>();
        [JsonProperty("contents")] public List<SvBundleLine> Contents { get; set; } = new List<SvBundleLine>();
        [JsonProperty("tags")] public List<string> Tags { get; set; } = new List<string>();
        /// <summary>The studio's JSON object for the item, or null. Never secrets.</summary>
        [JsonProperty("custom")] public JObject Custom { get; set; }
        /// <summary>This player may buy it (<see cref="SvStoreApi.PurchaseAsync"/>).</summary>
        [JsonProperty("purchasable")] public bool Purchasable { get; set; }
        /// <summary>This player may use it up (<see cref="SvStoreApi.ConsumeInstanceAsync"/>, <see cref="SvStoreApi.ConsumeSkuAsync"/>).</summary>
        [JsonProperty("consumable")] public bool Consumable { get; set; }

        /// <summary>The unit price in <paramref name="currency"/>, or null when the item has none.</summary>
        public long? PriceIn(string currency)
        {
            if (Prices == null) return null;
            foreach (var p in Prices)
                if (p != null && p.Currency == currency) return p.Amount;
            return null;
        }
    }

    /// <summary>The catalog for the signed-in player (<c>GET /store/catalog</c>).</summary>
    [Preserve]
    public sealed class SvStoreCatalog
    {
        [JsonProperty("settings")] public SvStoreSettings Settings { get; set; }
        [JsonProperty("currencies")] public List<SvCurrency> Currencies { get; set; } = new List<SvCurrency>();
        [JsonProperty("items")] public List<SvCatalogItem> Items { get; set; } = new List<SvCatalogItem>();

        public SvCatalogItem Find(string sku)
        {
            if (Items == null) return null;
            foreach (var i in Items)
                if (i != null && i.Sku == sku) return i;
            return null;
        }
    }

    [Preserve]
    public sealed class SvBalance
    {
        [JsonProperty("currency")] public string Currency { get; set; }
        [JsonProperty("amount")] public long Amount { get; set; }
    }

    /// <summary>An inventory row: one stack, or one copy of a one-of-a-kind item.</summary>
    [Preserve]
    public sealed class SvInventoryItem
    {
        [JsonProperty("instanceId")] public string InstanceId { get; set; }
        [JsonProperty("sku")] public string Sku { get; set; }
        [JsonProperty("quantity")] public long Quantity { get; set; }
        [JsonProperty("custom")] public JObject Custom { get; set; }
        [JsonProperty("acquiredAt")] public string AcquiredAt { get; set; }
    }

    [Preserve]
    public sealed class SvStorePlayer
    {
        [JsonProperty("id")] public string Id { get; set; }
    }

    /// <summary>The signed-in player's balances and items (<c>GET /store/me</c>).</summary>
    [Preserve]
    public sealed class SvStoreHoldings
    {
        [JsonProperty("player")] public SvStorePlayer Player { get; set; }
        [JsonProperty("balances")] public List<SvBalance> Balances { get; set; } = new List<SvBalance>();
        [JsonProperty("items")] public List<SvInventoryItem> Items { get; set; } = new List<SvInventoryItem>();

        /// <summary>The balance of <paramref name="currency"/> (0 when the player holds none).</summary>
        public long BalanceOf(string currency)
        {
            if (Balances == null) return 0;
            foreach (var b in Balances)
                if (b != null && b.Currency == currency) return b.Amount;
            return 0;
        }

        /// <summary>How many of <paramref name="sku"/> the player holds, all stacks and copies together.</summary>
        public long CountOf(string sku)
        {
            long n = 0;
            if (Items == null) return 0;
            foreach (var i in Items)
                if (i != null && i.Sku == sku) n += i.Quantity;
            return n;
        }
    }

    /// <summary>A ledger change: exactly one of <see cref="Currency"/> and <see cref="Sku"/>.</summary>
    [Preserve]
    public sealed class SvLedgerChange
    {
        [JsonProperty("currency")] public string Currency { get; set; }
        [JsonProperty("sku")] public string Sku { get; set; }
        [JsonProperty("instanceId")] public string InstanceId { get; set; }
        [JsonProperty("delta")] public long Delta { get; set; }
        /// <summary>The balance, stack quantity, or 1 / 0 for a one-of-a-kind copy, after the change.</summary>
        [JsonProperty("after")] public long After { get; set; }
    }

    [Preserve]
    public sealed class SvShortfall
    {
        [JsonProperty("currency")] public string Currency { get; set; }
        [JsonProperty("sku")] public string Sku { get; set; }
        [JsonProperty("missing")] public long Missing { get; set; }
    }

    [Preserve]
    public sealed class SvTxn
    {
        [JsonProperty("id")] public string Id { get; set; }
        /// <summary><c>grant</c>, <c>consume</c>, <c>purchase</c>, <c>platform_purchase</c>, <c>reversal</c>, <c>adjust</c>, <c>update_item</c>, <c>import</c>, <c>erase</c>.</summary>
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("createdAt")] public string CreatedAt { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
        [JsonProperty("reverses")] public string Reverses { get; set; }
    }

    /// <summary>
    /// The answer of a store change. Trust its balances and items, not your own arithmetic.
    /// <see cref="Replayed"/> true means the API had already applied this change (same Idempotency-Key): it is
    /// still a success.
    /// </summary>
    [Preserve]
    public sealed class SvStoreTxnResult
    {
        [JsonProperty("txn")] public SvTxn Txn { get; set; }
        [JsonProperty("changes")] public List<SvLedgerChange> Changes { get; set; } = new List<SvLedgerChange>();
        /// <summary>The balances this change touched, after it.</summary>
        [JsonProperty("balances")] public List<SvBalance> Balances { get; set; } = new List<SvBalance>();
        /// <summary>The inventory rows this change touched that still exist, after it.</summary>
        [JsonProperty("items")] public List<SvInventoryItem> Items { get; set; } = new List<SvInventoryItem>();
        [JsonProperty("shortfall")] public List<SvShortfall> Shortfall { get; set; } = new List<SvShortfall>();
        [JsonProperty("replayed")] public bool Replayed { get; set; }

        /// <summary>The Idempotency-Key this change was sent with (set by the SDK).</summary>
        [JsonIgnore] public string IdempotencyKey { get; set; }
    }

    /// <summary>A ledger entry of the player's history (newest first).</summary>
    [Preserve]
    public sealed class SvHistoryEntry
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("createdAt")] public string CreatedAt { get; set; }
        [JsonProperty("reverses")] public string Reverses { get; set; }
        [JsonProperty("changes")] public List<SvLedgerChange> Changes { get; set; } = new List<SvLedgerChange>();
        [JsonProperty("shortfall")] public List<SvShortfall> Shortfall { get; set; } = new List<SvShortfall>();
    }

    /// <summary>One page (50) of the player's store history.</summary>
    [Preserve]
    public sealed class SvStoreHistoryPage
    {
        [JsonProperty("txns")] public List<SvHistoryEntry> Txns { get; set; } = new List<SvHistoryEntry>();
        /// <summary>Pass as <c>before</c> for the next, older page; null at the end.</summary>
        [JsonProperty("next")] public string Next { get; set; }
    }

    /// <summary>The Meta Quest purchase check's counts.</summary>
    [Preserve]
    public sealed class SvMetaSyncResult
    {
        [JsonProperty("granted")] public int Granted { get; set; }
        [JsonProperty("alreadyGranted")] public int AlreadyGranted { get; set; }
        [JsonProperty("unmapped")] public int Unmapped { get; set; }
        [JsonProperty("consumePending")] public int ConsumePending { get; set; }
    }

    /// <summary>A store change the player committed that has not been answered yet (kept in secure storage).</summary>
    [Preserve]
    public sealed class SvPendingStoreChange
    {
        [JsonProperty("key")] public string IdempotencyKey { get; set; }
        /// <summary><c>purchase</c> or <c>consume</c>.</summary>
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
        /// <summary>The exact JSON body first sent (a repeat must be the same request).</summary>
        [JsonProperty("body")] public string Body { get; set; }
        [JsonProperty("playerId")] public string PlayerId { get; set; }
        [JsonProperty("createdAt")] public string CreatedAt { get; set; }
    }
}
