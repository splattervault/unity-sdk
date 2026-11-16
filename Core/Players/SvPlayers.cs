using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SplatterVault.Core;

namespace SplatterVault.Players
{
    /// <summary>
    /// What the platform supplies when <see cref="SvPlayersOptions"/> leaves it out. The Unity package
    /// (SplatterVault.Runtime) fills these in at startup: a UnityWebRequest transport, the platform's secure
    /// storage and Debug.Log. Elsewhere, pass them in the options.
    /// </summary>
    public static class SvPlayersDefaults
    {
        /// <summary>Makes the HTTP transport.</summary>
        public static Func<ISvHttpTransport> TransportFactory { get; set; }

        /// <summary>Makes the secure store; the argument names the store ("splattervault").</summary>
        public static Func<string, ISecureStore> SecureStoreFactory { get; set; }

        public static SvLogger Logger { get; set; }

        /// <summary>When set, player services do not work on this platform and <see cref="SvPlayers"/> refuses to start with this message.</summary>
        public static string UnsupportedPlatformMessage { get; set; }
    }

    /// <summary>How an <see cref="SvPlayers"/> is set up. Name the game with exactly one of <see cref="GameId"/> and <see cref="GameKey"/>.</summary>
    public sealed class SvPlayersOptions
    {
        /// <summary>Your game id (the number on the game console's Sign-in tab). It never changes: prefer it.</summary>
        public int GameId { get; set; }

        /// <summary>The game's Game Key. It changes when the game moves to another owner.</summary>
        public string GameKey { get; set; }

        /// <summary>The API, <c>/rest</c> included.</summary>
        public string BaseUrl { get; set; } = SvPlayers.DefaultBaseUrl;

        /// <summary>
        /// DEVELOPMENT ONLY, off by default: allows an <c>http://</c> base URL whose host is not loopback. Without it only
        /// <c>https://</c> and loopback hosts (localhost, 127.0.0.0/8, ::1) are accepted, because player tokens, passwords
        /// and guest secrets travel on it. Redirects are never followed either way.
        /// </summary>
        public bool AllowInsecureHttp { get; set; }

        /// <summary>HTTP transport; default: <see cref="SvPlayersDefaults.TransportFactory"/>.</summary>
        public ISvHttpTransport Transport { get; set; }

        /// <summary>Where refresh tokens, guest secrets and pending store changes are kept; default: the platform's.</summary>
        public ISecureStore SecureStore { get; set; }

        /// <summary>
        /// Separates this game's stored secrets from others in the same store. Default (fixed, it will not change in
        /// later versions): the API's origin (scheme, host and port, lower-case; the path and a trailing slash are
        /// ignored) and the game (its id, or its key). Set it to keep a guest when you switch from
        /// <see cref="GameKey"/> to <see cref="GameId"/>, or move the API to another host.
        /// </summary>
        public string StorageNamespace { get; set; }

        public SvRetryPolicy RetryPolicy { get; set; } = new SvRetryPolicy();

        /// <summary>How long one HTTP request may take: more than 0, at most 2^31 - 1 ms.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long one refresh send may take. Short on purpose: a refresh token is spent when the API reads it, and
        /// a lost answer may be resent only within 30 seconds; with 8 s per send every resend lands in time. More than 0,
        /// at most 10 s.
        /// </summary>
        public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromSeconds(8);

        /// <summary>A renewable sign-in (email, guest) is renewed this long before its player token runs out: 0 to 30 minutes.</summary>
        public TimeSpan RenewBeforeExpiry { get; set; } = TimeSpan.FromSeconds(120);

        /// <summary>Default: <see cref="SvPlayersDefaults.Logger"/>.</summary>
        public SvLogger Logger { get; set; }

        /// <summary>The wall clock (tests).</summary>
        public Func<DateTimeOffset> Clock { get; set; }

        /// <summary>A monotonic clock: time since some fixed moment, never moved by a time sync (tests; default a Stopwatch).</summary>
        public Func<TimeSpan> MonotonicClock { get; set; }

        /// <summary>Waits between repeats (tests).</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay { get; set; }
    }

    /// <summary>
    /// The game client's entry point to SplatterVault's studio services: player sign-in and accounts
    /// (<see cref="Auth"/>), player data (<see cref="Data"/>) and the store (<see cref="Store"/>).
    /// <para>It never takes an API key: a game build must never hold one. Players sign in with a platform proof,
    /// a SplatterVault email account or a guest on the device, and every request then carries the player token.
    /// Make one per game and keep it for the whole session.</para>
    /// </summary>
    public sealed class SvPlayers
    {
        public const string DefaultBaseUrl = "https://splattervault.com/rest";

        /// <summary>For game <paramref name="gameId"/>, with the platform's transport and secure storage.</summary>
        public SvPlayers(int gameId, string baseUrl = DefaultBaseUrl)
            : this(new SvPlayersOptions { GameId = gameId, BaseUrl = baseUrl })
        {
        }

        public SvPlayers(SvPlayersOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var hasId = options.GameId != 0;
            var hasKey = !string.IsNullOrEmpty(options.GameKey);
            if (hasId == hasKey) throw new ArgumentException("Name the game with exactly one of GameId or GameKey.", nameof(options));
            if (hasId && options.GameId < 0) throw new ArgumentException("GameId must be a positive number.", nameof(options));
            CheckOptions(options);

            BaseUrl = CheckBaseUrl(options.BaseUrl, options.AllowInsecureHttp);
            GameId = hasId ? options.GameId : 0;
            GameKey = hasKey ? options.GameKey : null;

            var log = options.Logger ?? SvPlayersDefaults.Logger;
            var transport = options.Transport;
            if (transport == null)
            {
                if (SvPlayersDefaults.UnsupportedPlatformMessage != null)
                    throw new PlatformNotSupportedException(SvPlayersDefaults.UnsupportedPlatformMessage);
                transport = SvPlayersDefaults.TransportFactory?.Invoke();
                if (transport == null)
                    throw new InvalidOperationException(
                        "No HTTP transport. In Unity the SplatterVault.Runtime assembly registers one when the game starts; " +
                        "elsewhere set SvPlayersOptions.Transport.");
            }

            var store = options.SecureStore;
            if (store == null && SvPlayersDefaults.SecureStoreFactory != null)
            {
                try
                {
                    store = SvPlayersDefaults.SecureStoreFactory("splattervault");
                }
                catch (Exception e)
                {
                    // It exists but cannot be opened: unreadable, never an empty store (that would make a new guest over
                    // the stored one). Platform sign-ins still work; stored ones read as storage_unreadable.
                    SvLog.Write(log, SvLogLevel.Error, "The platform's secure storage could not be opened (" + e.GetType().Name +
                                                      "): stored sign-ins read as storage_unreadable.");
                    store = new SvUnreadableSecureStore("The platform's secure storage could not be opened.", e);
                }
            }
            if (store == null)
            {
                SvLog.Write(log, SvLogLevel.Warning,
                    "No secure storage: refresh tokens and guest secrets are kept in memory only and are lost when the game quits.");
                store = new SvMemorySecureStore();
            }

            var clock = options.Clock ?? (() => DateTimeOffset.UtcNow);
            var mono = options.MonotonicClock;
            if (mono == null)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                mono = () => stopwatch.Elapsed;
            }
            var delay = options.Delay ?? ((wait, ct) => Task.Delay(wait, ct));
            var timeout = options.RequestTimeout;
            Pipeline = new SvPipeline(BaseUrl, transport, options.RetryPolicy, timeout, clock, mono, delay, log);
            SecureStore = store;
            StoragePrefix = StoragePrefixFor(options.StorageNamespace ?? DefaultStorageNamespace(BaseUrl, GameId, GameKey));

            Auth = new SvAuth(this, options.RenewBeforeExpiry, options.RefreshTimeout);
            Data = new SvPlayerDataApi(this);
            Store = new SvStoreApi(this);
        }

        /// <summary>The game id, or 0 when the game is named by <see cref="GameKey"/>.</summary>
        public int GameId { get; }

        public string GameKey { get; }

        public string BaseUrl { get; }

        /// <summary>Sign-in, accounts and the player token.</summary>
        public SvAuth Auth { get; }

        /// <summary>The player's saved data and the game's public data.</summary>
        public SvPlayerDataApi Data { get; }

        /// <summary>Currencies, catalog, the player's inventory, purchases and consumption.</summary>
        public SvStoreApi Store { get; }

        internal SvPipeline Pipeline { get; }

        internal ISecureStore SecureStore { get; }

        internal string StoragePrefix { get; }

        internal void AddGame(Newtonsoft.Json.Linq.JObject body)
        {
            if (GameId != 0) body["gameId"] = GameId;
            else body["gameKey"] = GameKey;
        }

        /// <summary>The longest refresh send (see <see cref="SvPlayersOptions.RefreshTimeout"/>).</summary>
        public static readonly TimeSpan MaxRefreshTimeout = TimeSpan.FromSeconds(10);

        /// <summary>The longest renewal lead (see <see cref="SvPlayersOptions.RenewBeforeExpiry"/>).</summary>
        public static readonly TimeSpan MaxRenewBeforeExpiry = TimeSpan.FromMinutes(30);

        /// <summary>Numeric options out of range are refused here, never clamped quietly at the first request.</summary>
        private static void CheckOptions(SvPlayersOptions options)
        {
            if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > SvRetryPolicy.MaxWait)
                throw new ArgumentOutOfRangeException(nameof(options.RequestTimeout), options.RequestTimeout,
                    "RequestTimeout is more than 0 and at most " + SvRetryPolicy.MaxWait.TotalMilliseconds + " ms.");
            if (options.RefreshTimeout <= TimeSpan.Zero || options.RefreshTimeout > MaxRefreshTimeout)
                throw new ArgumentOutOfRangeException(nameof(options.RefreshTimeout), options.RefreshTimeout,
                    "RefreshTimeout is more than 0 and at most 10 s (every resend of a refresh must land within the API's 30-second grace).");
            if (options.RenewBeforeExpiry < TimeSpan.Zero || options.RenewBeforeExpiry > MaxRenewBeforeExpiry)
                throw new ArgumentOutOfRangeException(nameof(options.RenewBeforeExpiry), options.RenewBeforeExpiry,
                    "RenewBeforeExpiry is 0 to 30 minutes.");
        }

        private static string CheckBaseUrl(string baseUrl, bool allowInsecure)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("BaseUrl is required.");
            if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new ArgumentException("BaseUrl must be an absolute http(s) URL, such as " + DefaultBaseUrl + ".");
            if (uri.Scheme == "http" && !allowInsecure && !uri.IsLoopback)
                throw new ArgumentException("BaseUrl must use https (player tokens and secrets travel on it).");
            return baseUrl.Trim().TrimEnd('/');
        }

        /// <summary>
        /// The default storage namespace: <c>scheme://host[:port]</c> (lower-case, no path) and the game. Fixed: a
        /// change would orphan every stored guest.
        /// </summary>
        internal static string DefaultStorageNamespace(string baseUrl, int gameId, string gameKey)
        {
            var uri = new Uri(baseUrl);
            var origin = uri.Scheme.ToLowerInvariant() + "://" + uri.Host.ToLowerInvariant() +
                         (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return origin + "|" + (gameId != 0 ? "id:" + gameId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "key:" + gameKey);
        }

        internal static string StoragePrefixFor(string storageNamespace)
        {
            return "sv4." + SvHash.Hex(SvHash.Sha256(Encoding.UTF8.GetBytes(storageNamespace))).Substring(0, 16) + ".";
        }
    }
}
