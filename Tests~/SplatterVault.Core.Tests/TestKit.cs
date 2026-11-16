using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SplatterVault.Core;
using SplatterVault.Players;

namespace SplatterVault.Core.Tests
{
    /// <summary>A transport that answers from a handler and records every request (and an ordering log).</summary>
    internal sealed class FakeTransport : ISvHttpTransport
    {
        public const string Base = "https://api.test/rest";
        private readonly object _gate = new object();
        public readonly List<SvHttpRequest> Requests = new List<SvHttpRequest>();
        public List<string> Events = new List<string>();
        public Func<SvHttpRequest, Task<SvHttpResponse>> Handler = r => Task.FromResult(Http.Json(404, new JObject { ["error"] = "not_set_up" }));

        public Task<SvHttpResponse> SendAsync(SvHttpRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Requests.Add(request);
                lock (Events)
                    Events.Add("send " + request.Method + " " + PathOf(request) + " " + (request.Headers.TryGetValue("Authorization", out var a) ? a : "-"));
            }
            return Handler(request);
        }

        public static string PathOf(SvHttpRequest r) => r.Url.StartsWith(Base) ? r.Url.Substring(Base.Length) : r.Url;

        public List<SvHttpRequest> To(string method, string pathPrefix)
        {
            lock (_gate)
                return Requests.Where(r => r.Method == method && PathOf(r).StartsWith(pathPrefix)).ToList();
        }

        /// <summary>Requests to exactly this path (query ignored).</summary>
        public List<SvHttpRequest> Exactly(string method, string path)
        {
            lock (_gate)
                return Requests.Where(r => r.Method == method && PathOf(r).Split('?')[0] == path).ToList();
        }
    }

    /// <summary>A memory store that writes what it does into a shared ordering log.</summary>
    internal sealed class RecordingStore : ISecureStore
    {
        private readonly SvMemorySecureStore _inner = new SvMemorySecureStore();
        public List<string> Events;

        public RecordingStore(List<string> events)
        {
            Events = events;
        }

        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();

        public Task<string> GetAsync(string key) => _inner.GetAsync(key);

        public async Task SetAsync(string key, string value)
        {
            lock (Events) Events.Add("store set " + Suffix(key) + "=" + value);
            lock (Values) Values[Suffix(key)] = value;
            await _inner.SetAsync(key, value);
        }

        public async Task DeleteAsync(string key)
        {
            lock (Events) Events.Add("store delete " + Suffix(key));
            lock (Values) Values.Remove(Suffix(key));
            await _inner.DeleteAsync(key);
        }

        public string Get(string suffix)
        {
            lock (Values) return Values.TryGetValue(suffix, out var v) ? v : null;
        }

        /// <summary>The stored refresh token (the record is { v, token, player }).</summary>
        public string RefreshToken() => Field("refresh_token", "token", "svpr1_");

        /// <summary>The player the stored refresh token belongs to.</summary>
        public string RefreshOwner() => Field("refresh_token", "player", "svpr1_");

        private string Field(string suffix, string name, string barePrefix)
        {
            var raw = Get(suffix);
            if (raw == null) return null;
            if (raw.StartsWith(barePrefix)) return name == "token" || name == "secret" ? raw : null;
            return (string)JObject.Parse(raw)[name];
        }

        /// <summary>The stored guest secret (the record is { v, secret, player }).</summary>
        public string GuestSecret() => GuestField("secret");

        /// <summary>The player the stored guest secret belongs to.</summary>
        public string GuestOwner() => GuestField("player");

        private string GuestField(string name)
        {
            var raw = Get("device_secret");
            if (raw == null) return null;
            if (raw.StartsWith("svpd1_")) return name == "secret" ? raw : null;
            return (string)JObject.Parse(raw)[name];
        }

        private static string Suffix(string key) => key.Substring(key.LastIndexOf('.') + 1);
    }

    /// <summary>A store whose reads, writes or deletes fail for chosen keys (by suffix), like a locked keychain.</summary>
    internal sealed class FlakyStore : ISecureStore
    {
        private readonly ISecureStore _inner;
        public readonly HashSet<string> FailReads = new HashSet<string>();
        public readonly HashSet<string> FailWrites = new HashSet<string>();
        public readonly HashSet<string> FailDeletes = new HashSet<string>();
        public readonly List<string> Writes = new List<string>();
        /// <summary>A write makes the key readable again (a value sealed on another device, then overwritten).</summary>
        public bool HealOnWrite;

        public FlakyStore(ISecureStore inner)
        {
            _inner = inner;
        }

        private static string Suffix(string key) => key.Substring(key.LastIndexOf('.') + 1);

        public Task<string> GetAsync(string key)
        {
            if (FailReads.Contains(Suffix(key))) throw new SvSecureStoreException("locked");
            return _inner.GetAsync(key);
        }

        public Task SetAsync(string key, string value)
        {
            if (FailWrites.Contains(Suffix(key))) throw new SvSecureStoreException("read-only");
            lock (Writes) Writes.Add(Suffix(key));
            if (HealOnWrite) FailReads.Remove(Suffix(key));
            return _inner.SetAsync(key, value);
        }

        public readonly List<string> DeleteAttempts = new List<string>();

        public Task DeleteAsync(string key)
        {
            lock (DeleteAttempts) DeleteAttempts.Add(Suffix(key));
            if (FailDeletes.Contains(Suffix(key))) throw new SvSecureStoreException("read-only");
            return _inner.DeleteAsync(key);
        }

        public Task<string> RawAsync(string key) => _inner.GetAsync(key);
    }

    internal static class Http
    {
        /// <summary>An unsigned JWT-shaped token with exactly these claims.</summary>
        public static string JwtWith(JObject claims)
        {
            string B64(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return B64("{\"alg\":\"EdDSA\"}") + "." + B64(claims.ToString(Newtonsoft.Json.Formatting.None)) + ".sig";
        }

        /// <summary>An unsigned JWT-shaped token with these claims (the SDK reads iat/exp/amr/sub without verifying).</summary>
        public static string Jwt(long iat, long exp, string sub = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b", string amr = null, string tag = "")
        {
            var claims = new JObject { ["sub"] = sub, ["iat"] = iat, ["exp"] = exp, ["tag"] = tag };
            if (amr != null) claims["amr"] = new JArray(amr);
            string B64(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return B64("{\"alg\":\"EdDSA\"}") + "." + B64(claims.ToString(Newtonsoft.Json.Formatting.None)) + ".sig";
        }

        public static SvHttpResponse Json(int status, JToken body, params (string, string)[] headers)
        {
            var h = headers.ToDictionary(x => x.Item1, x => x.Item2);
            return new SvHttpResponse(status, body == null ? "" : body.ToString(Newtonsoft.Json.Formatting.None), h);
        }

        public static SvHttpResponse Error(int status, string code, string message = "A sentence.", params (string, string)[] headers)
        {
            return Json(status, new JObject { ["error"] = code, ["message"] = message }, headers);
        }

        public static Task<SvHttpResponse> Done(SvHttpResponse r) => Task.FromResult(r);

        public static Task<SvHttpResponse> NetworkFailure()
        {
            var t = new TaskCompletionSource<SvHttpResponse>();
            t.SetException(new SvTransportException(SvTransportFailure.Network, "connection reset"));
            return t.Task;
        }

        /// <summary>A sign-in answer as the API sends it (PlayerAuthGuards.issue plus the account extras).</summary>
        public static JObject SignIn(string token, string expiresAt = "2026-11-14T13:00:00.000Z", string refresh = null, string device = null,
            string playerId = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b", string provider = "email")
        {
            var o = new JObject
            {
                ["token"] = token,
                ["expiresAt"] = expiresAt,
                ["player"] = new JObject { ["id"] = playerId, ["displayName"] = null, ["createdAt"] = "2026-11-01T00:00:00.000Z", ["newPlayer"] = false },
                ["identities"] = new JArray(new JObject { ["provider"] = provider, ["linkedAt"] = "2026-11-01T00:00:00.000Z" })
            };
            if (refresh != null)
            {
                o["refreshToken"] = refresh;
                o["refreshExpiresAt"] = "2026-12-14T12:00:00.000Z";
            }
            if (device != null) o["deviceSecret"] = device;
            return o;
        }

        public static JObject Holdings() => new JObject
        {
            ["player"] = new JObject { ["id"] = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b" },
            ["balances"] = new JArray(new JObject { ["currency"] = "GOLD", ["amount"] = 700 }),
            ["items"] = new JArray()
        };

        public static JObject Txn(bool replayed) => new JObject
        {
            ["txn"] = new JObject { ["id"] = "5b0e0000-0000-4000-8000-000000000001", ["kind"] = "purchase", ["createdAt"] = "2026-11-13T19:00:00.000Z", ["reason"] = null, ["reverses"] = null },
            ["changes"] = new JArray(
                new JObject { ["currency"] = "GOLD", ["delta"] = -500, ["after"] = 700 },
                new JObject { ["sku"] = "sword_gold", ["instanceId"] = "c2a10000-0000-4000-8000-000000000002", ["delta"] = 1, ["after"] = 1 }),
            ["balances"] = new JArray(new JObject { ["currency"] = "GOLD", ["amount"] = 700 }),
            ["items"] = new JArray(new JObject
            {
                ["instanceId"] = "c2a10000-0000-4000-8000-000000000002", ["sku"] = "sword_gold", ["quantity"] = 1, ["custom"] = null,
                ["acquiredAt"] = "2026-11-13T19:00:00.000Z"
            }),
            ["shortfall"] = new JArray(),
            ["replayed"] = replayed
        };
    }

    /// <summary>An SvPlayers wired to a fake transport, a store, a fake clock and instant (recorded) waits.</summary>
    internal sealed class Kit
    {
        public readonly FakeTransport Transport = new FakeTransport();
        public readonly List<TimeSpan> Waits = new List<TimeSpan>();
        public readonly List<string> Logs = new List<string>();
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        /// <summary>The monotonic clock (a time sync moves only <see cref="Now"/>).</summary>
        public TimeSpan Mono = TimeSpan.FromHours(10);

        /// <summary>Time passes: both clocks move.</summary>
        public void Advance(TimeSpan t)
        {
            Now += t;
            Mono += t;
        }
        public ISecureStore Store;
        public RecordingStore Recording;
        public SvPlayers Sv;

        public Kit(Action<SvPlayersOptions> tweak = null, ISecureStore store = null)
        {
            Recording = new RecordingStore(Transport.Events);
            Store = store ?? Recording;
            var options = new SvPlayersOptions
            {
                GameId = 123,
                BaseUrl = FakeTransport.Base,
                Transport = Transport,
                SecureStore = Store,
                RetryPolicy = new SvRetryPolicy { Jitter = 0 },
                Clock = () => Now,
                MonotonicClock = () => Mono,
                Delay = (wait, ct) =>
                {
                    lock (Waits) Waits.Add(wait);
                    Advance(wait);
                    return Task.CompletedTask;
                },
                Logger = (level, message) =>
                {
                    lock (Logs) Logs.Add(level + " " + message);
                }
            };
            tweak?.Invoke(options);
            Sv = new SvPlayers(options);
        }

        public static JObject Body(SvHttpRequest r) => r.Body == null ? null : (JObject)SvJson.Parse(r.Body);

        /// <summary>Signs in by email password with a fixed answer.</summary>
        public async Task SignInByEmailAsync(string token = "t1", string refresh = "svpr1_A", string expiresAt = "2026-11-14T13:00:00.000Z")
        {
            var previous = Transport.Handler;
            Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn(token, expiresAt, refresh)));
            await Sv.Auth.SignInWithPasswordAsync("nova@example.com", "correct horse");
            Transport.Handler = previous;
        }

        public async Task SignInBySteamAsync(string token = "t1", string expiresAt = "2026-11-14T13:00:00.000Z")
        {
            var previous = Transport.Handler;
            Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn(token, expiresAt, provider: "steam")));
            await Sv.Auth.SignInWithSteamAsync("ABCDEF0123");
            Transport.Handler = previous;
        }
    }
}
