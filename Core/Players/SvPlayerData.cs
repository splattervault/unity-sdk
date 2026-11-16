using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SplatterVault.Core;

namespace SplatterVault.Players
{
    /// <summary>
    /// Ops for one player-data write (<see cref="SvPlayerDataApi.WriteAsync"/>), applied all or nothing, in order.
    /// Players write only their <c>player</c> store. At most 25 ops.
    /// <code>
    /// var ops = new SvDataOps()
    ///     .Set("settings", new Settings { fov = 90 }, ifVersion: data.Player.VersionOf("settings"))
    ///     .Delete("tutorial")
    ///     .Inc("matches", 1);
    /// </code>
    /// <para><c>ifVersion</c>: the key must still be at that version (0: must not exist yet), else nothing is written
    /// and the answer is <c>version_conflict</c> with the current versions. The API has no Idempotency-Key for data
    /// writes. When EVERY op has <c>ifVersion</c>, the SDK sends a write again by itself only after an answer that
    /// proves nothing was applied (408, 429, 503, or a renewed token after 401 <c>token_expired</c>). After no answer,
    /// a 500, 502 or 504 the write may have been applied: <see cref="SvApiException.IsOutcomeUnknown"/> is true and it is
    /// not sent again. A repeat answered <c>version_conflict</c> is reported as <c>outcome_unknown</c>, never as a plain
    /// conflict. Recover by reading again: when the key's version moved and the value is yours, the write applied.</para>
    /// </summary>
    public sealed class SvDataOps
    {
        /// <summary>The most ops one player write may hold.</summary>
        public const int MaxOps = 25;

        private const string PlayerStore = "player";
        private readonly List<JObject> _ops = new List<JObject>();

        public int Count => _ops.Count;

        /// <summary>Every op carries <c>ifVersion</c> (and so may be repeated safely).</summary>
        public bool AllHaveIfVersion => _ops.Count > 0 && _ops.All(o => o["ifVersion"] != null);

        /// <summary>Stores <paramref name="value"/> (any JSON but null: delete the key instead) under <paramref name="key"/>.</summary>
        public SvDataOps Set<T>(string key, T value, long? ifVersion = null)
        {
            var token = SvJson.FromObject(value);
            if (token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                throw new ArgumentException("null is not a value: delete the key instead.", nameof(value));
            var op = Op("set", key, ifVersion);
            op["value"] = token;
            return Add(op);
        }

        /// <summary>Deletes <paramref name="key"/> (its version still goes up).</summary>
        public SvDataOps Delete(string key, long? ifVersion = null)
        {
            return Add(Op("delete", key, ifVersion));
        }

        /// <summary>
        /// Adds <paramref name="by"/> (negative to subtract) to a whole-number value (a missing one counts as 0),
        /// or to its whole-number <paramref name="field"/>. With <paramref name="min"/> / <paramref name="max"/> the
        /// result must stay within them (<c>out_of_range</c> otherwise). The answer holds the new value.
        /// </summary>
        public SvDataOps Inc(string key, long by, string field = null, long? min = null, long? max = null, long? ifVersion = null)
        {
            if (min.HasValue && max.HasValue && min.Value > max.Value) throw new ArgumentException("min must not be above max.");
            var op = Op("inc", key, ifVersion);
            op["by"] = by;
            if (field != null) op["field"] = CheckKey(field, nameof(field));
            if (min.HasValue) op["min"] = min.Value;
            if (max.HasValue) op["max"] = max.Value;
            return Add(op);
        }

        /// <summary>The request body: <c>{ "ops": [ ... ] }</c>.</summary>
        public JObject ToBody()
        {
            return new JObject { ["ops"] = new JArray(_ops.Select(o => (JToken)o.DeepClone())) };
        }

        public string ToJson() => SvJson.Serialize(ToBody());

        private SvDataOps Add(JObject op)
        {
            if (_ops.Count >= MaxOps) throw new InvalidOperationException("A player write holds at most " + MaxOps + " ops.");
            _ops.Add(op);
            return this;
        }

        private static JObject Op(string name, string key, long? ifVersion)
        {
            var op = new JObject
            {
                ["op"] = name,
                ["store"] = PlayerStore,
                ["key"] = CheckKey(key, nameof(key))
            };
            if (ifVersion.HasValue)
            {
                if (ifVersion.Value < 0) throw new ArgumentOutOfRangeException(nameof(ifVersion), "ifVersion is 0 or more.");
                op["ifVersion"] = ifVersion.Value;
            }
            return op;
        }

        /// <summary>1 to 128 letters, digits and _ . : - (starting with a letter, digit or _); sv. and __proto__ are reserved.</summary>
        public static bool IsValidKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 128) return false;
            if (key == "__proto__" || key.StartsWith("sv.", StringComparison.OrdinalIgnoreCase)) return false;
            for (var i = 0; i < key.Length; i++)
            {
                var c = key[i];
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' ||
                         (i > 0 && (c == '.' || c == ':' || c == '-'));
                if (!ok) return false;
            }
            return true;
        }

        private static string CheckKey(string key, string name)
        {
            if (!IsValidKey(key))
                throw new ArgumentException("Keys are 1 to 128 letters, digits, _ . : - (not starting with . : -); sv. and __proto__ are reserved.", name);
            return key;
        }
    }

    /// <summary>Player data (<c>/rest/player-data/*</c>): the player's own stores and the game's public data.</summary>
    public sealed class SvPlayerDataApi
    {
        /// <summary>The most keys one read may name.</summary>
        public const int MaxReadKeys = 100;

        private readonly SvPlayers _sv;
        private readonly object _gate = new object();
        private readonly Dictionary<string, SvGameData> _etagCache = new Dictionary<string, SvGameData>(StringComparer.Ordinal);

        internal SvPlayerDataApi(SvPlayers sv)
        {
            _sv = sv;
        }

        /// <summary>
        /// The signed-in player's <c>player</c> and <c>readonly</c> stores (or only <paramref name="stores"/>), every
        /// key or only <paramref name="keys"/> (at most 100); <paramref name="keysOnly"/> leaves values out.
        /// </summary>
        public async Task<SvPlayerData> GetMineAsync(IEnumerable<string> keys = null, bool keysOnly = false, IEnumerable<string> stores = null,
            CancellationToken ct = default(CancellationToken))
        {
            var query = new List<string>();
            var storeList = stores?.ToList();
            if (storeList != null && storeList.Count > 0)
            {
                foreach (var s in storeList)
                    if (s != "player" && s != "readonly")
                        throw new ArgumentException("A player reads only the player and readonly stores.", nameof(stores));
                query.Add("stores=" + string.Join(",", storeList));
            }
            var keyParam = KeysParam(keys);
            if (keyParam != null) query.Add(keyParam);
            if (keysOnly) query.Add("keysOnly=true");
            var path = "/player-data/me" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
            var response = await _sv.Auth.SendAsPlayerAsync(new SvCall { Method = "GET", Path = path, Retry = SvRetry.Safe }, ct);
            var answer = SvPipeline.Read<PlayerDataAnswer>(response);
            var result = new Dictionary<string, SvDataStore>(StringComparer.Ordinal);
            if (answer.Data != null)
                foreach (var kv in answer.Data)
                    result[kv.Key] = new SvDataStore(Ordinal(kv.Value));
            return new SvPlayerData(result);
        }

        /// <summary>
        /// Applies <paramref name="ops"/> to the player's <c>player</c> store, all or nothing. Sent again by the SDK only
        /// when every op has <c>ifVersion</c> and the answer proved nothing was applied (408, 429, 503). When the outcome
        /// is unknown (no answer, 500, 502, 504, or a repeat answered with a conflict) the exception has
        /// <see cref="SvApiException.IsOutcomeUnknown"/>: read the data again before writing again. Never re-apply a
        /// change (add one more win) just because a write failed. 30 writes a minute per player.
        /// </summary>
        public async Task<SvDataWriteResult> WriteAsync(SvDataOps ops, CancellationToken ct = default(CancellationToken))
        {
            if (ops == null) throw new ArgumentNullException(nameof(ops));
            if (ops.Count == 0) throw new ArgumentException("Add at least one op.", nameof(ops));
            var call = new SvCall
            {
                Method = "PATCH",
                Path = "/player-data/me",
                Body = ops.ToBody(),
                Retry = ops.AllHaveIfVersion ? SvRetry.IfNotApplied : SvRetry.Never
            };
            var response = await _sv.Auth.SendAsPlayerAsync(call, ct);
            return SvPipeline.Read<SvDataWriteResult>(response);
        }

        /// <summary>
        /// The game's <c>public</c> data, as the signed-in player. The SDK keeps the last answer and its ETag and
        /// asks with <c>If-None-Match</c>: when nothing changed the API answers 304 and the kept copy is returned
        /// (<see cref="SvGameData.NotModified"/>). Changes reach players within about 30 seconds; don't poll.
        /// </summary>
        public Task<SvGameData> GetGameDataAsync(IEnumerable<string> keys = null, CancellationToken ct = default(CancellationToken))
        {
            var keyParam = KeysParam(keys);
            var path = "/player-data/game" + (keyParam != null ? "?" + keyParam : "");
            return GetCachedAsync(path, true, ct);
        }

        /// <summary>
        /// A game's <c>public</c> data with no credential (before sign-in, a launcher): <paramref name="gameIdOrKey"/>
        /// is a game id or Game Key, default this game. A missing game and one without player data get the same
        /// <c>game_not_found</c>. 120 a minute per address; cached up to 30 seconds by the API.
        /// </summary>
        public Task<SvGameData> GetPublicAsync(string gameIdOrKey = null, IEnumerable<string> keys = null,
            CancellationToken ct = default(CancellationToken))
        {
            var game = gameIdOrKey ?? (_sv.GameId != 0 ? _sv.GameId.ToString(System.Globalization.CultureInfo.InvariantCulture) : _sv.GameKey);
            if (string.IsNullOrEmpty(game)) throw new ArgumentException("Name the game.", nameof(gameIdOrKey));
            var keyParam = KeysParam(keys);
            var path = "/player-data/games/" + Uri.EscapeDataString(game) + "/public" + (keyParam != null ? "?" + keyParam : "");
            return GetCachedAsync(path, false, ct);
        }

        private async Task<SvGameData> GetCachedAsync(string path, bool asPlayer, CancellationToken ct)
        {
            SvGameData cached;
            lock (_gate) _etagCache.TryGetValue(path, out cached);
            var call = new SvCall { Method = "GET", Path = path, Retry = SvRetry.Safe, Allow304 = cached != null };
            if (cached != null && cached.ETag != null)
                call.Headers = new Dictionary<string, string> { ["If-None-Match"] = cached.ETag };
            var response = asPlayer
                ? await _sv.Auth.SendAsPlayerAsync(call, ct)
                : await _sv.Pipeline.SendAsync(call, null, ct);
            if (response.Status == 304 && cached != null) return new SvGameData(cached.Data, cached.ETag, true);
            var answer = SvPipeline.Read<GameDataAnswer>(response);
            var etag = response.GetHeader("ETag");
            var fresh = new SvGameData(new SvDataStore(Ordinal(answer.Data)), etag, false);
            lock (_gate)
            {
                if (etag != null) _etagCache[path] = fresh;
                else _etagCache.Remove(path);
            }
            return fresh;
        }

        private static Dictionary<string, SvDataEntry> Ordinal(Dictionary<string, SvDataEntry> source)
        {
            var d = new Dictionary<string, SvDataEntry>(StringComparer.Ordinal);
            if (source != null)
                foreach (var kv in source)
                    d[kv.Key] = kv.Value;
            return d;
        }

        private static string KeysParam(IEnumerable<string> keys)
        {
            if (keys == null) return null;
            var list = keys.Distinct(StringComparer.Ordinal).ToList();
            if (list.Count == 0) return null;
            if (list.Count > MaxReadKeys) throw new ArgumentException("A read names at most " + MaxReadKeys + " keys.", nameof(keys));
            foreach (var k in list)
                if (!SvDataOps.IsValidKey(k))
                    throw new ArgumentException("Not a valid key: keys are 1 to 128 letters, digits, _ . : -.", nameof(keys));
            return "keys=" + string.Join(",", list.Select(Uri.EscapeDataString));
        }
    }
}
