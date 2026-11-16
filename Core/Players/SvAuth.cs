using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SplatterVault.Core;

namespace SplatterVault.Players
{
    /// <summary>
    /// Player sign-in and the player's account (<c>/rest/player-auth/*</c>).
    /// <para><b>Tokens.</b> The player token (1 hour) lives in memory only (<see cref="PlayerToken"/>). The refresh
    /// token of an email sign-in and a guest's device secret go to secure storage and are never handed out. Every
    /// player request carries the token; when it runs out the SDK renews it by itself where it can: an email
    /// sign-in with its refresh token (one refresh at a time; the new refresh token is saved before the new player
    /// token is used), a guest with this device's secret. Other sign-ins (Meta, Steam, Xsolla, PlayFab, Supabase,
    /// your backend) need a fresh proof: the SDK raises <see cref="SignInRequired"/> and, when
    /// <see cref="Reauthenticate"/> is set, awaits it and sends the request again once.</para>
    /// <para><b>Expiry</b> is measured on this device's clock: the token's lifetime (its <c>exp</c> minus its
    /// <c>iat</c>, else one hour) counted from when the sign-in request was sent, so a device clock that is off by
    /// hours changes nothing.</para>
    /// <para><b>Storage.</b> A secure store that cannot be read is never taken as "nothing stored": the SDK then
    /// refuses to make a new guest over the old one (<c>storage_unreadable</c>) and reads again next time.</para>
    /// </summary>
    public sealed class SvAuth
    {
        private const string TokenExpiredCode = "token_expired";
        private const string RefreshItem = "refresh_token";
        private const string GuestItem = "guest_secret";
        private const string LastItem = "last_sign_in";
        private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);
        private static readonly TimeSpan MinLifetime = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);
        private static readonly TimeSpan ClockMargin = TimeSpan.FromSeconds(5);

        private readonly SvPlayers _sv;
        private readonly TimeSpan _renewLead;
        private readonly TimeSpan _refreshTimeout;
        private readonly object _gate = new object();
        private readonly SvSingleFlight<string> _quietRenew = new SvSingleFlight<string>();
        private readonly SvSingleFlight<string> _askGame = new SvSingleFlight<string>();
        /// <summary>First-launch guest sign-in: concurrent callers share one, so one device never makes two guests.</summary>
        private readonly SvSingleFlight<SvSignInResult> _guestFlight = new SvSingleFlight<SvSignInResult>();
        /// <summary>Serializes everything that may make or replace this device's guest.</summary>
        private readonly SemaphoreSlim _guestLock = new SemaphoreSlim(1, 1);

        private Session _session;
        /// <summary>
        /// Who-is-signed-in generation: bumped by every sign-in the game asks for, sign-out, erase and cleared session
        /// (a renewal of the same session keeps it). A renewal or restore remembers it when it starts; an answer that
        /// arrives under another epoch is not accepted (rule 11 of the cross-SDK rules).
        /// </summary>
        private long _epoch;
        private string _lastMethod;
        private string _deadToken;
        private bool _deviceRejected;
        private SvSignInRequiredReason _lastRenewReason = SvSignInRequiredReason.TokenExpired;
        private string _lastRenewCode;
        private SvApiException _lastRenewError;

        private bool _refreshLoaded;
        private RefreshRecord _refresh;
        private bool _refreshUnsaved;
        private bool _deviceLoaded;
        private DeviceRecord _device;
        private bool _deviceUnsaved;

        private sealed class Session
        {
            public string Token;
            public string ExpiresAt;
            /// <summary>On this device's wall clock.</summary>
            public DateTimeOffset? Expiry;
            /// <summary>When a renewable sign-in is renewed ahead of expiry (device wall clock).</summary>
            public DateTimeOffset? RenewAt;
            /// <summary>The same two moments on the monotonic clock (a time sync cannot move them).</summary>
            public TimeSpan? MonoExpiry;
            public TimeSpan? MonoRenewAt;
            public string Method;
            public SvSignedInPlayer Player;
            public List<SvIdentity> Identities;
            /// <summary>Set when the API ended this sign-in's renewal (refresh_invalid, a refused guest secret).</summary>
            public SvSignInRequiredReason? EndReason;
            public string EndCode;
        }

        /// <summary>The stored refresh token and the player it belongs to (null for one stored without it: never used).</summary>
        private sealed class RefreshRecord
        {
            public string Token;
            public string PlayerId;
        }

        /// <summary>A moment on both clocks.</summary>
        private struct Stamp
        {
            public DateTimeOffset Wall;
            public TimeSpan Mono;
        }

        /// <summary>The guest secret on this device and the player it belongs to (null for one stored without it).</summary>
        private sealed class DeviceRecord
        {
            public string Secret;
            public string PlayerId;
        }

        private sealed class LastSignIn
        {
            /// <summary>null: signed out on this device.</summary>
            public string Method;
            public string PlayerId;
        }

        private enum RenewMode
        {
            /// <summary>Refresh token, else (a guest session) the device secret.</summary>
            Auto,
            /// <summary>The refresh token only.</summary>
            RefreshOnly,
            /// <summary>The refresh token only, even when the current token is still fine.</summary>
            ForceRefresh
        }

        internal SvAuth(SvPlayers sv, TimeSpan renewLead, TimeSpan refreshTimeout)
        {
            _sv = sv;
            _renewLead = renewLead < TimeSpan.Zero ? TimeSpan.Zero : renewLead;
            _refreshTimeout = refreshTimeout;
        }

        // ─── state ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A player is signed in (their token may still need renewing).</summary>
        public bool IsSignedIn => Current() != null;

        /// <summary>
        /// The player token, for your own game servers to check (offline, with <c>GET /rest/player-auth/keys</c>).
        /// Keep it in memory: never log it or write it anywhere.
        /// </summary>
        public string PlayerToken => Current()?.Token;

        /// <summary>The SplatterVault player id (the token's <c>sub</c>), or null.</summary>
        public string PlayerId => Current()?.Player?.Id;

        /// <summary>The signed-in player, or null.</summary>
        public SvSignedInPlayer Player => Current()?.Player;

        /// <summary>How the player signed in (<see cref="SvProviders"/>), or null.</summary>
        public string Method => Current()?.Method;

        /// <summary>The ways the player can sign in, as last answered.</summary>
        public IReadOnlyList<SvIdentity> Identities => (IReadOnlyList<SvIdentity>)Current()?.Identities ?? Array.Empty<SvIdentity>();

        /// <summary>When the player token runs out as the API said it (ISO 8601, the API's clock), or null.</summary>
        public string TokenExpiresAt => Current()?.ExpiresAt;

        /// <summary>When the player token runs out on THIS device's clock (what the SDK uses), or null.</summary>
        public DateTimeOffset? TokenExpiresAtUtc => Current()?.Expiry;

        /// <summary>A player signed in (any way, renewals included).</summary>
        public event Action<SvSignInResult> SignedIn;

        /// <summary>The player signed out, or the sign-in ended (see <see cref="SignInRequired"/>).</summary>
        public event Action SignedOut;

        /// <summary>
        /// The player must sign in again with a fresh proof (the token ran out and cannot be renewed, or the API
        /// ended the sign-in). Raised once per ended sign-in.
        /// </summary>
        public event Action<SvSignInRequired> SignInRequired;

        /// <summary>
        /// Secure storage could not read, write or delete something (it never carries a value). A failed write of
        /// a new guest secret means that guest exists in memory only: tell the player, or call
        /// <see cref="RetrySavingSecretsAsync"/>.
        /// </summary>
        public event Action<SvStorageFailure> StorageFailed;

        /// <summary>
        /// Optional: called (once, with requests waiting) when a player token runs out and cannot be renewed. Sign
        /// the player in again inside it (for example get a new Steam ticket and call
        /// <see cref="SignInWithSteamAsync(string, string, CancellationToken)"/>) and return true: the waiting
        /// requests are then sent again once with the new token. Return false to let them fail with
        /// <c>token_expired</c>.
        /// </summary>
        public Func<SvSignInRequired, CancellationToken, Task<bool>> Reauthenticate { get; set; }

        private string RefreshKey => _sv.StoragePrefix + "refresh_token";
        private string DeviceKey => _sv.StoragePrefix + "device_secret";
        private string LastKey => _sv.StoragePrefix + "last_sign_in";

        // ─── restore ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Signs the player back in without asking them, the way they signed in last on this device: an email
        /// sign-in with its stored refresh token, a guest with this device's secret. Never another player: after
        /// a Meta, Steam, Xsolla, PlayFab, Supabase or studio sign-in, or a sign-out, this answers false even when a
        /// guest is stored (sign in with that platform again: <see cref="GetLastSignInMethodAsync"/> says which).
        /// False also when nothing is stored or the API refused it. A network failure throws
        /// (<see cref="SvApiException.IsNetworkError"/>), and unreadable storage throws <c>storage_unreadable</c>,
        /// so "offline" or "storage locked" is never "signed out".
        /// </summary>
        public async Task<bool> RestoreAsync(CancellationToken ct = default(CancellationToken))
        {
            // A sign-in or sign-out by the game while this runs wins: the restore's answer is then not accepted.
            var epoch = Epoch;
            var s = Current();
            if (s != null && !IsExpired(s) && s.Token != _deadToken) return true;
            var last = await ReadLastSignInAsync(true);
            if (last == null || last.Method == null) return false; // nobody signed in here, or they signed out
            var method = last.Method;
            if (method == SvProviders.Email)
            {
                // Only that player's own refresh token (see UsableRefreshTokenAsync).
                if (await UsableRefreshTokenAsync(s, last, epoch) == null) return false;
                var token = await _quietRenew.RunAsync(() => RenewQuietCoreAsync(s?.Token, RenewMode.RefreshOnly, epoch), ct);
                return token != null && Epoch == epoch;
            }
            if (method != SvProviders.Device) return false;
            var device = await GetDeviceAsync();
            if (device == null || _deviceRejected) return false;
            if (device.PlayerId != null && last.PlayerId != null && device.PlayerId != last.PlayerId) return false;
            try
            {
                return await DeviceSignInCoreAsync(device, ct, epoch) != null;
            }
            catch (SvApiException e) when (e.Is("proof_invalid"))
            {
                return false;
            }
        }

        /// <summary>
        /// How the player last signed in on this device (<see cref="SvProviders"/>), or null when nobody did or they
        /// signed out. Use it at start-up to sign a platform player in again with that platform.
        /// </summary>
        public async Task<string> GetLastSignInMethodAsync()
        {
            return (await ReadLastSignInAsync(true))?.Method;
        }

        /// <summary>
        /// Saves again a refresh token or guest secret that secure storage could not save earlier (they are kept in
        /// memory meanwhile). True when nothing is left unsaved.
        /// </summary>
        public async Task<bool> RetrySavingSecretsAsync()
        {
            RefreshRecord refresh;
            bool refreshUnsaved;
            DeviceRecord device;
            bool deviceUnsaved;
            lock (_gate)
            {
                refresh = _refresh;
                refreshUnsaved = _refreshUnsaved;
                device = _device;
                deviceUnsaved = _deviceUnsaved;
            }
            var ok = true;
            if (refreshUnsaved && refresh != null) ok &= await SaveRefreshTokenAsync(refresh.Token, refresh.PlayerId);
            if (deviceUnsaved && device != null) ok &= await SaveDeviceAsync(device.Secret, device.PlayerId);
            return ok;
        }

        // ─── platform proofs ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Meta Quest: <paramref name="userId"/> from <c>Users.GetLoggedInUser()</c> (the 64-bit id as a string) and
        /// <paramref name="nonce"/> from <c>Users.GetUserProof()</c>. A nonce works once: get a new one for every
        /// sign-in, also after <c>provider_unavailable</c>.
        /// </summary>
        public Task<SvSignInResult> SignInWithMetaAsync(string userId, string nonce, string appId = null,
            CancellationToken ct = default(CancellationToken))
        {
            return ExchangeAsync(SvProviders.Meta, MetaProof(userId, nonce), appId, ct);
        }

        /// <summary>Meta Quest, with the id as <c>User.ID</c> gives it.</summary>
        public Task<SvSignInResult> SignInWithMetaAsync(ulong userId, string nonce, string appId = null,
            CancellationToken ct = default(CancellationToken))
        {
            return SignInWithMetaAsync(userId.ToString(CultureInfo.InvariantCulture), nonce, appId, ct);
        }

        /// <summary>
        /// Steam: the hex ticket from <c>SteamUser.GetAuthTicketForWebApi("splattervault")</c>. A ticket works once;
        /// cancel it with Steam after this answers.
        /// </summary>
        public Task<SvSignInResult> SignInWithSteamAsync(string ticketHex, string appId = null,
            CancellationToken ct = default(CancellationToken))
        {
            return ExchangeAsync(SvProviders.Steam, SteamProof(ticketHex), appId, ct);
        }

        /// <summary>Steam, with the ticket bytes of <c>GetTicketForWebApiResponse_t</c>.</summary>
        public Task<SvSignInResult> SignInWithSteamAsync(byte[] ticket, int length, string appId = null,
            CancellationToken ct = default(CancellationToken))
        {
            return SignInWithSteamAsync(ToHex(ticket, length), appId, ct);
        }

        /// <summary>Xsolla: the player's Xsolla Login token. <paramref name="appId"/> is the Login project id.</summary>
        public Task<SvSignInResult> SignInWithXsollaAsync(string token, string appId = null,
            CancellationToken ct = default(CancellationToken))
        {
            return ExchangeAsync(SvProviders.Xsolla, new JObject { ["token"] = Required(token, nameof(token)) }, appId, ct);
        }

        /// <summary>PlayFab: the <c>SessionTicket</c> of the player's PlayFab login. <paramref name="appId"/> is the title id.</summary>
        public Task<SvSignInResult> SignInWithPlayFabAsync(string sessionTicket, string appId = null,
            CancellationToken ct = default(CancellationToken))
        {
            return ExchangeAsync(SvProviders.PlayFab, new JObject { ["sessionTicket"] = Required(sessionTicket, nameof(sessionTicket)) },
                appId, ct);
        }

        /// <summary>Supabase: the player's access token. <paramref name="appId"/> is the project ref.</summary>
        public Task<SvSignInResult> SignInWithSupabaseAsync(string accessToken, string appId = null,
            CancellationToken ct = default(CancellationToken))
        {
            return ExchangeAsync(SvProviders.Supabase, new JObject { ["token"] = Required(accessToken, nameof(accessToken)) }, appId, ct);
        }

        /// <summary>
        /// Uses a player token your backend got from <c>POST /rest/player-auth/studio</c> (with its organization key,
        /// on your server). The token is checked with <c>GET /player-auth/me</c> first: a refused one changes
        /// nothing. It cannot be renewed: when it runs out, <see cref="SignInRequired"/> is raised and your backend
        /// issues a new one. <paramref name="expiresAt"/> (the API's <c>expiresAt</c>) is optional: the token's
        /// <c>exp</c> is used otherwise, corrected for this device's clock with the answer's <c>Date</c>.
        /// </summary>
        public async Task<SvPlayerProfile> SignInWithStudioTokenAsync(string playerToken, string expiresAt = null,
            CancellationToken ct = default(CancellationToken))
        {
            Required(playerToken, nameof(playerToken));
            var claims = ReadClaims(playerToken);
            var method = SvProviders.Studio;
            if (claims != null && claims["amr"] is JArray amr && amr.Count > 0 && amr[0].Type == JTokenType.String) method = (string)amr[0];
            var sub = claims != null && claims["sub"] is JValue s && s.Type == JTokenType.String ? (string)s : null;
            var serverExpiry = SvDates.Parse(expiresAt);
            var exp = Seconds(claims?["exp"]);
            if (serverExpiry == null && exp.HasValue) serverExpiry = FromUnix(exp.Value);

            // Checked BEFORE anything stored is touched: a refused token leaves the current sign-in as it was.
            var response = await _sv.Pipeline.SendAsync(new SvCall { Method = "GET", Path = "/player-auth/me", Retry = SvRetry.Safe },
                playerToken, ct);
            var arrived = Now();
            var answer = SvPipeline.Read<ProfileAnswer>(response);
            // From here this sign-in owns the session: a renewal landing while it saves is stale.
            var epoch = NewEpoch();

            // The token's age is unknown (your backend issued it), so its expiry is the API's: corrected for this
            // device's clock with the answer's Date when there is one, else taken as it is.
            DateTimeOffset? localExpiry = serverExpiry;
            var serverNow = ParseHttpDate(response.GetHeader("Date"));
            if (serverExpiry.HasValue && serverNow.HasValue) localExpiry = serverExpiry.Value - (serverNow.Value - arrived.Wall);
            TimeSpan? monoExpiry = null;
            if (localExpiry.HasValue) monoExpiry = arrived.Mono + (localExpiry.Value - arrived.Wall);

            await DropRefreshTokenAsync(true);
            var player = new SvSignedInPlayer
            {
                Id = answer.Player?.Id ?? sub,
                DisplayName = answer.Player?.DisplayName,
                CreatedAt = answer.Player?.CreatedAt
            };
            var session = new Session
            {
                Token = playerToken,
                ExpiresAt = expiresAt ?? serverExpiry?.ToString("o", CultureInfo.InvariantCulture),
                Expiry = localExpiry,
                MonoExpiry = monoExpiry,
                Method = method,
                Player = player,
                Identities = answer.Identities ?? new List<SvIdentity>()
            };
            if (!TrySetSession(session, epoch)) throw Superseded();
            await WriteLastSignInAsync(method, player.Id);
            RaiseSignedIn(ToResult(session, false, true));
            return Profile(answer);
        }

        // ─── email accounts ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Emails a one-time code (sign in, sign up, reset the password). Always answers the same, whether or not
        /// the address has an account. At most one a minute per address.
        /// </summary>
        public async Task<SvCodeSent> RequestEmailCodeAsync(string email, CancellationToken ct = default(CancellationToken))
        {
            var body = GameBody();
            body["email"] = Required(email, nameof(email));
            var response = await _sv.Pipeline.SendAsync(Post("/player-auth/email/code", body, SvRetry.Never), null, ct);
            return SvPipeline.Read<SvCodeSent>(response);
        }

        /// <summary>
        /// Signs in with the emailed code. With no account yet a player is made (when the game lets players sign
        /// up). <paramref name="newPassword"/> sets (or resets) the password and signs out the player's other
        /// devices. Renewable: the refresh token is kept in secure storage.
        /// </summary>
        public async Task<SvSignInResult> VerifyEmailCodeAsync(string email, string code, string newPassword = null,
            string displayName = null, CancellationToken ct = default(CancellationToken))
        {
            var body = GameBody();
            body["email"] = Required(email, nameof(email));
            body["code"] = Required(code, nameof(code));
            if (newPassword != null) body["password"] = newPassword;
            if (displayName != null) body["displayName"] = displayName;
            var started = Now();
            var response = await _sv.Pipeline.SendAsync(Post("/player-auth/email/code/verify", body, SvRetry.Never), null, ct);
            return await AcceptAsync(SvPipeline.Read<SignInAnswer>(response), SvProviders.Email, false, started);
        }

        /// <summary>Signs in with an email and password. A wrong pair is always <c>invalid_credentials</c>.</summary>
        public async Task<SvSignInResult> SignInWithPasswordAsync(string email, string password,
            CancellationToken ct = default(CancellationToken))
        {
            var body = GameBody();
            body["email"] = Required(email, nameof(email));
            body["password"] = Required(password, nameof(password));
            var started = Now();
            var response = await _sv.Pipeline.SendAsync(Post("/player-auth/email/password", body, SvRetry.Never), null, ct);
            return await AcceptAsync(SvPipeline.Read<SignInAnswer>(response), SvProviders.Email, false, started);
        }

        /// <summary>
        /// Renews an email sign-in now with its refresh token (the SDK also does this by itself before the token
        /// runs out). Throws <c>refresh_invalid</c> when the sign-in has ended.
        /// </summary>
        public async Task<SvSignInResult> RefreshAsync(CancellationToken ct = default(CancellationToken))
        {
            if (await UsableRefreshTokenAsync(Current(), null) == null)
                throw new InvalidOperationException("There is no email sign-in of this player to refresh on this device.");
            var used = Current()?.Token;
            var epoch = Epoch;
            var token = await _quietRenew.RunAsync(() => RenewQuietCoreAsync(used, RenewMode.ForceRefresh, epoch), ct);
            if (token == null)
                throw _lastRenewError ?? new SvApiException(401, "refresh_invalid", "This sign-in has ended. Sign in again.");
            return ToResult(Current(), IsRenewableNow(Current()), !_refreshUnsaved);
        }

        /// <summary>
        /// Signs out on this device: ends the email sign-in (its refresh token) and forgets the player token. The
        /// next <see cref="RestoreAsync"/> answers false. The guest secret stays unless <paramref name="forgetGuest"/>
        /// is true: it is the guest's only way back in, so forget it only when the player has another way to sign
        /// in (an email) or really wants a new guest.
        /// </summary>
        public async Task SignOutAsync(bool forgetGuest = false, CancellationToken ct = default(CancellationToken))
        {
            // First: a renewal answered while this runs must not sign the player back in.
            BumpEpoch();
            string refresh = null;
            try
            {
                refresh = (await GetRefreshRecordAsync())?.Token;
            }
            catch (SvApiException)
            {
                // Unreadable: it is deleted below; there is nothing to end on the server we could name.
            }
            await DeleteRefreshTokenAsync();
            if (forgetGuest) await ForgetGuestOnDeviceAsync();
            await WriteLastSignInAsync(null, null);
            ClearSession(true);
            if (refresh == null) return;
            try
            {
                await _sv.Pipeline.SendAsync(Post("/player-auth/sign-out", new JObject { ["refreshToken"] = refresh }, SvRetry.Never), null, ct);
            }
            catch (SvApiException e)
            {
                SvLog.Write(_sv.Pipeline.Log, SvLogLevel.Warning, "Signed out on this device; the server sign-out failed (" +
                                                                  (e.ErrorCode ?? e.Status.ToString(CultureInfo.InvariantCulture)) + ").");
            }
        }

        // ─── guests ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Signs in as this device's guest, making one on the first launch. The device secret is saved to secure
        /// storage before the player token is used; it is the guest's only way back in. When storage cannot be
        /// read this throws <c>storage_unreadable</c> and makes NO new guest (the old one may still be there);
        /// check <see cref="SvSignInResult.SavedToDevice"/> after a new guest.
        /// </summary>
        public Task<SvSignInResult> SignInAsGuestAsync(string displayName = null, CancellationToken ct = default(CancellationToken))
        {
            // Two callers at once (a first launch with two scripts) share ONE sign-in: never two guests.
            return _guestFlight.RunAsync(() => GuestLockedAsync(async () =>
            {
                var device = await GetDeviceAsync();
                if (device != null) return await DeviceSignInCoreAsync(device, CancellationToken.None);
                return await CreateGuestCoreAsync(displayName, CancellationToken.None);
            }), ct);
        }

        /// <summary>
        /// Makes a new guest. Refused when this device already has one (and <c>storage_unreadable</c> when storage
        /// cannot tell), unless <paramref name="replaceExistingGuest"/> is true: the old guest is then lost unless
        /// it has an email. That is also the way out when storage stays unreadable (for example a backup restored
        /// to another device, whose keys did not come along).
        /// </summary>
        public Task<SvSignInResult> CreateGuestAsync(string displayName = null, bool replaceExistingGuest = false,
            CancellationToken ct = default(CancellationToken))
        {
            return GuestLockedAsync(async () =>
            {
                if (!replaceExistingGuest && await GetDeviceAsync() != null)
                    throw new InvalidOperationException(
                        "This device already has a guest. Sign in with SignInAsGuestAsync, or pass replaceExistingGuest: true to make a new one.");
                return await CreateGuestCoreAsync(displayName, ct);
            });
        }

        /// <summary>Signs in with this device's guest secret. Throws <c>proof_invalid</c> when no guest has it any more.</summary>
        public Task<SvSignInResult> SignInWithDeviceAsync(CancellationToken ct = default(CancellationToken))
        {
            return GuestLockedAsync(async () =>
            {
                var device = await GetDeviceAsync();
                if (device == null) throw new InvalidOperationException("There is no guest on this device.");
                return await DeviceSignInCoreAsync(device, ct);
            });
        }

        private async Task<T> GuestLockedAsync<T>(Func<Task<T>> work)
        {
            await _guestLock.WaitAsync();
            try
            {
                return await work();
            }
            finally
            {
                _guestLock.Release();
            }
        }

        /// <summary>This device holds a guest secret. Throws <c>storage_unreadable</c> when storage cannot tell.</summary>
        public async Task<bool> HasGuestOnDeviceAsync()
        {
            return await GetDeviceAsync() != null;
        }

        /// <summary>Forgets this device's guest secret (the guest is lost unless it has another way to sign in).</summary>
        public async Task ForgetGuestOnDeviceAsync()
        {
            lock (_gate)
            {
                _device = null;
                _deviceLoaded = true;
                _deviceRejected = false;
                _deviceUnsaved = false;
            }
            if (!await DeleteKeyAsync(DeviceKey, GuestItem))
                lock (_gate) _deviceLoaded = false; // still stored: read it again next time
        }

        // ─── the signed-in player ───────────────────────────────────────────────────────────────────────────────

        /// <summary>The signed-in player, their game and their ways to sign in.</summary>
        public async Task<SvPlayerProfile> GetMeAsync(CancellationToken ct = default(CancellationToken))
        {
            var response = await SendAsPlayerAsync(new SvCall { Method = "GET", Path = "/player-auth/me", Retry = SvRetry.Safe }, ct);
            return Profile(SvPipeline.Read<ProfileAnswer>(response));
        }

        /// <summary>
        /// Erases the player and everything every studio service keeps about them (sign-in links, data in every
        /// game, store balances and items). It cannot be undone. Signs out on this device and forgets what this
        /// device kept for that player (its refresh token, its guest secret, its unanswered store changes).
        /// </summary>
        public async Task EraseMeAsync(CancellationToken ct = default(CancellationToken))
        {
            var playerId = Current()?.Player?.Id;
            var epoch = Epoch;
            try
            {
                await SendAsPlayerAsync(new SvCall { Method = "DELETE", Path = "/player-auth/me", Retry = SvRetry.Never }, ct);
            }
            catch (SvApiException e) when (e.Is("player_deleted"))
            {
                // Already erased (an earlier answer was lost): the outcome asked for.
            }
            // Someone who signed in (or out) during the DELETE keeps what is theirs: only this player's records go.
            await ForgetErasedPlayerAsync(playerId, Epoch == epoch);
            lock (_gate)
                if (_epoch != epoch) return;
            ClearSession(true);
        }

        /// <summary>Links a Meta account to the signed-in player (a fresh nonce, as for sign-in).</summary>
        public Task<SvPlayerProfile> LinkMetaAsync(string userId, string nonce, string appId = null, CancellationToken ct = default(CancellationToken))
        {
            return LinkAsync(SvProviders.Meta, MetaProof(userId, nonce), appId, ct);
        }

        public Task<SvPlayerProfile> LinkSteamAsync(string ticketHex, string appId = null, CancellationToken ct = default(CancellationToken))
        {
            return LinkAsync(SvProviders.Steam, SteamProof(ticketHex), appId, ct);
        }

        public Task<SvPlayerProfile> LinkXsollaAsync(string token, string appId = null, CancellationToken ct = default(CancellationToken))
        {
            return LinkAsync(SvProviders.Xsolla, new JObject { ["token"] = Required(token, nameof(token)) }, appId, ct);
        }

        public Task<SvPlayerProfile> LinkPlayFabAsync(string sessionTicket, string appId = null, CancellationToken ct = default(CancellationToken))
        {
            return LinkAsync(SvProviders.PlayFab, new JObject { ["sessionTicket"] = Required(sessionTicket, nameof(sessionTicket)) }, appId, ct);
        }

        public Task<SvPlayerProfile> LinkSupabaseAsync(string accessToken, string appId = null, CancellationToken ct = default(CancellationToken))
        {
            return LinkAsync(SvProviders.Supabase, new JObject { ["token"] = Required(accessToken, nameof(accessToken)) }, appId, ct);
        }

        /// <summary>
        /// Unlinks a way to sign in: xsolla, meta, steam, playfab, supabase or device (unlinking the device also
        /// forgets its secret here, when it is this player's). The email has its own call
        /// (<see cref="RemoveEmailAsync"/>); the studio link cannot be removed by the player. Refused
        /// (<c>last_identity</c>) for the player's only way in.
        /// </summary>
        public async Task<SvPlayerProfile> UnlinkAsync(string provider, CancellationToken ct = default(CancellationToken))
        {
            Required(provider, nameof(provider));
            if (provider == SvProviders.Email) throw new ArgumentException("Remove the email with RemoveEmailAsync (it needs proof).", nameof(provider));
            if (provider == SvProviders.Studio) throw new ArgumentException("Only your backend can remove the studio link.", nameof(provider));
            var playerId = PlayerId;
            var response = await SendAsPlayerAsync(new SvCall
            {
                Method = "DELETE",
                Path = "/player-auth/link/" + Uri.EscapeDataString(provider),
                Retry = SvRetry.Never
            }, ct);
            var profile = Profile(SvPipeline.Read<ProfileAnswer>(response));
            if (provider == SvProviders.Device) await ForgetDeviceIfOwnedByAsync(playerId);
            return profile;
        }

        /// <summary>Emails a code to <paramref name="email"/> to add it (or change to it). Then <see cref="AddEmailAsync"/> or <see cref="ChangeEmailAsync"/>.</summary>
        public async Task<SvCodeSent> RequestLinkEmailCodeAsync(string email, CancellationToken ct = default(CancellationToken))
        {
            var body = new JObject { ["email"] = Required(email, nameof(email)) };
            var response = await SendAsPlayerAsync(Post("/player-auth/link/email/code", body, SvRetry.Never), ct);
            return SvPipeline.Read<SvCodeSent>(response);
        }

        /// <summary>Emails a code to the CURRENT address of an account without a password: the <c>oldCode</c> that <see cref="RemoveEmailAsync"/> needs.</summary>
        public async Task<SvCodeSent> RequestEmailRemovalCodeAsync(CancellationToken ct = default(CancellationToken))
        {
            var response = await SendAsPlayerAsync(Post("/player-auth/link/email/code", new JObject { ["unlink"] = true }, SvRetry.Never), ct);
            return SvPipeline.Read<SvCodeSent>(response);
        }

        /// <summary>
        /// Adds a verified email to the signed-in player (a guest keeps their player), with an optional password.
        /// <c>identity_in_use</c>: another player of the studio has that email.
        /// </summary>
        public Task<SvPlayerProfile> AddEmailAsync(string email, string code, string password = null,
            CancellationToken ct = default(CancellationToken))
        {
            var body = new JObject { ["email"] = Required(email, nameof(email)), ["code"] = Required(code, nameof(code)) };
            if (password != null) body["password"] = password;
            return LinkEmailAsync(body, ct);
        }

        /// <summary>
        /// Changes the email: <paramref name="code"/> from the new address, plus <paramref name="currentPassword"/>
        /// (an account with a password) or <paramref name="oldCode"/> (sent to the current address). Signs out the
        /// player's other devices; this device keeps its sign-in (a new refresh token is saved).
        /// </summary>
        public Task<SvPlayerProfile> ChangeEmailAsync(string newEmail, string code, string currentPassword = null, string oldCode = null,
            CancellationToken ct = default(CancellationToken))
        {
            var body = new JObject { ["email"] = Required(newEmail, nameof(newEmail)), ["code"] = Required(code, nameof(code)) };
            if (currentPassword != null) body["currentPassword"] = currentPassword;
            if (oldCode != null) body["oldCode"] = oldCode;
            return LinkEmailAsync(body, ct);
        }

        /// <summary>
        /// Removes the email (and its password): <paramref name="currentPassword"/>, or for an account without a
        /// password <paramref name="oldCode"/> from <see cref="RequestEmailRemovalCodeAsync"/>. Every email sign-in
        /// of the player ends, this device's too.
        /// </summary>
        /// <remarks>
        /// Sent as <c>POST /player-auth/link/email/remove</c>: some HTTP stacks drop or refuse a DELETE body, and this
        /// request needs its body. An API without that route answers 404; the SDK then sends the same body once as
        /// <c>DELETE /player-auth/link/email</c>, the older form.
        /// </remarks>
        public async Task<SvPlayerProfile> RemoveEmailAsync(string currentPassword = null, string oldCode = null,
            CancellationToken ct = default(CancellationToken))
        {
            var body = new JObject();
            if (currentPassword != null) body["currentPassword"] = currentPassword;
            if (oldCode != null) body["oldCode"] = oldCode;
            SvHttpResponse response;
            try
            {
                response = await SendAsPlayerAsync(Post("/player-auth/link/email/remove", body, SvRetry.Never), ct);
            }
            catch (SvApiException e) when (IsRouteMissing(e))
            {
                // Nothing was removed (the route does not exist there): the older API's form, once.
                response = await SendAsPlayerAsync(new SvCall
                {
                    Method = "DELETE",
                    Path = "/player-auth/link/email",
                    Body = body,
                    Retry = SvRetry.Never
                }, ct);
            }
            var profile = Profile(SvPipeline.Read<ProfileAnswer>(response));
            await DeleteRefreshTokenAsync();
            return profile;
        }

        /// <summary>
        /// A 404 for a route the API does not have: <c>not_found</c>, or an older API's framework answer
        /// (<c>{ "name": "NOT_FOUND" }</c>, no <c>error</c> code) or a bare 404. Never a 404 with another error code.
        /// </summary>
        private static bool IsRouteMissing(SvApiException e)
        {
            return e.Status == 404 && (e.ErrorCode == null || e.Is("not_found"));
        }

        /// <summary>
        /// Changes the password (never sets a first one: use <see cref="VerifyEmailCodeAsync"/> with a new password).
        /// Signs out the player's other devices; this device keeps its sign-in (a new refresh token is saved).
        /// </summary>
        public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default(CancellationToken))
        {
            var body = new JObject
            {
                ["currentPassword"] = Required(currentPassword, nameof(currentPassword)),
                ["newPassword"] = Required(newPassword, nameof(newPassword))
            };
            var playerId = PlayerId;
            var epoch = Epoch;
            var response = await SendAsPlayerAsync(new SvCall
            {
                Method = "PUT",
                Path = "/player-auth/me/password",
                Body = body,
                Retry = SvRetry.Never
            }, ct);
            var grant = SvPipeline.Read<RefreshGrantAnswer>(response);
            if (string.IsNullOrEmpty(grant.RefreshToken)) return;
            // Kept only while that player is still the one signed in here; otherwise the new sign-in is ended.
            if (Epoch == epoch && playerId != null && PlayerId == playerId) await SaveRefreshTokenAsync(grant.RefreshToken, playerId);
            else await SignOutQuietlyAsync(grant.RefreshToken);
        }

        /// <summary>A Steam ticket's bytes as the hex the API takes.</summary>
        public static string ToHex(byte[] bytes, int length)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (length < 0 || length > bytes.Length) throw new ArgumentOutOfRangeException(nameof(length));
            var sb = new StringBuilder(length * 2);
            for (var i = 0; i < length; i++) sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // ─── the request path for player routes ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a player request: renews the token first when it is about to run out (renewable sign-ins), and
        /// after a 401 <c>token_expired</c> renews once and sends once more. With <paramref name="expectedPlayerId"/>
        /// (store changes) it is sent only with that player's token: when another player is signed in by then, nothing
        /// is sent and <c>player_changed</c> (status 0) is thrown.
        /// </summary>
        internal async Task<SvHttpResponse> SendAsPlayerAsync(SvCall call, CancellationToken ct, string expectedPlayerId = null)
        {
            // Sent only as the player who was signed in when the call was made: never with another player's token.
            if (expectedPlayerId == null) expectedPlayerId = Current()?.Player?.Id;
            var token = await TokenForRequestAsync(ct);
            CheckPlayer(token, expectedPlayerId);
            var epoch = Epoch;
            try
            {
                return await _sv.Pipeline.SendAsync(call, token, ct);
            }
            catch (SvApiException e) when (e.Status == 401 && e.Is(TokenExpiredCode))
            {
                var renewed = await RenewAfterExpiryAsync(token, epoch, ct);
                if (renewed == null) throw;
                // The renewal (or the game's Reauthenticate) may have signed in someone else meanwhile.
                CheckPlayer(renewed, expectedPlayerId);
                try
                {
                    return await _sv.Pipeline.SendAsync(call, renewed, ct);
                }
                catch (SvApiException again) when (again.Status == 401)
                {
                    OnTokenRefused(renewed, again);
                    throw;
                }
            }
            catch (SvApiException e) when (e.Status == 401)
            {
                OnTokenRefused(token, e);
                throw;
            }
        }

        /// <summary>Throws <c>player_changed</c> unless <paramref name="token"/> is the current session's and it is <paramref name="expectedPlayerId"/>'s.</summary>
        private void CheckPlayer(string token, string expectedPlayerId)
        {
            if (expectedPlayerId == null) return;
            var s = Current();
            if (s != null && s.Token == token && string.Equals(s.Player?.Id, expectedPlayerId, StringComparison.Ordinal)) return;
            throw new SvApiException(0, SvApiException.PlayerChangedCode, null);
        }

        private async Task<string> TokenForRequestAsync(CancellationToken ct)
        {
            var s = Current();
            if (s == null) throw new SvApiException(0, SvApiException.NotSignedInCode, null);
            // Already refused by the API and not renewable: nothing is sent (status 0), so a dead sign-in never spends
            // the player's 60 requests a minute.
            if (s.Token == _deadToken) throw new SvApiException(0, TokenExpiredCode, "Token expired. Sign in again.");
            var epoch = Epoch;
            if (IsDue(s.RenewAt, s.MonoRenewAt) && s.EndReason == null && await IsRenewableQuietAsync(s))
            {
                try
                {
                    var renewed = await _quietRenew.RunAsync(() => RenewQuietCoreAsync(s.Token, RenewMode.Auto, epoch), ct);
                    if (renewed != null) return renewed;
                }
                catch (SvApiException e) when ((e.IsTransient || e.Is(SvApiException.StorageUnreadableCode)) && !IsExpired(s))
                {
                    // Not renewed this time; the current token still works for a little while.
                }
                s = Current();
                if (s == null) throw new SvApiException(0, SvApiException.NotSignedInCode, null);
            }
            return s.Token;
        }

        private async Task<string> RenewAfterExpiryAsync(string usedToken, long epoch, CancellationToken ct)
        {
            var token = await _quietRenew.RunAsync(() => RenewQuietCoreAsync(usedToken, RenewMode.Auto, epoch), ct);
            if (token != null) return token;
            return await _askGame.RunAsync(() => AskGameCoreAsync(usedToken, epoch), ct);
        }

        /// <summary>
        /// Renews without the player: the refresh token, else (a guest session, <see cref="RenewMode.Auto"/>) the
        /// device secret of that same guest. The new token, or null when neither works. Network failures, other
        /// refusals and unreadable storage propagate (nothing stored is dropped).
        /// </summary>
        private async Task<string> RenewQuietCoreAsync(string usedToken, RenewMode mode, long epoch)
        {
            // Signed out, or another player signed in, since the caller looked: nothing to renew.
            if (Epoch != epoch) return null;
            var cur = Current();
            if (mode != RenewMode.ForceRefresh && cur != null && cur.Token != usedToken && cur.Token != _deadToken && !IsExpired(cur))
                return cur.Token;
            _lastRenewReason = cur?.EndReason ?? SvSignInRequiredReason.TokenExpired;
            _lastRenewCode = cur?.EndCode ?? TokenExpiredCode;
            _lastRenewError = null;

            // Only this player's own refresh token: a leftover of another player (one whose delete failed) would
            // silently switch the session to that player. A refresh_invalid may race a newer token saved meanwhile (a
            // changed password, a changed email): that one is tried once.
            string sent = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                // After every await: a sign-in or sign-out meanwhile ends this renewal (its stored token is not ours).
                if (Epoch != epoch) return null;
                // Read again from storage first: another client sharing this store (a second player process, Multiplayer
                // Play Mode) may have rotated the refresh token since; sending the spent one would end the sign-in.
                ForgetCachedRefreshToken();
                var refresh = await UsableRefreshTokenAsync(cur, null, epoch);
                if (Epoch != epoch) return null;
                if (refresh == null || refresh == sent) break;
                sent = refresh;
                try
                {
                    return await RefreshCoreAsync(refresh, epoch) ? Current()?.Token : null;
                }
                catch (SvApiException e) when (e.Is("refresh_invalid"))
                {
                    await DeleteRefreshTokenIfAsync(refresh);
                    MarkEnded(cur, SvSignInRequiredReason.RefreshInvalid, e);
                }
            }

            if (mode != RenewMode.Auto || Epoch != epoch) return null;
            var method = cur?.Method ?? _lastMethod;
            if (method != SvProviders.Device || _deviceRejected) return null;
            var device = await GetDeviceAsync();
            if (device == null || Epoch != epoch) return null;
            // Only that guest's own secret renews a guest session: never another player's.
            var playerId = cur?.Player?.Id;
            if (playerId != null && device.PlayerId != null && device.PlayerId != playerId) return null;
            try
            {
                return await DeviceSignInCoreAsync(device, CancellationToken.None, epoch) != null ? Current()?.Token : null;
            }
            catch (SvApiException e) when (e.Is("proof_invalid"))
            {
                MarkEnded(cur, SvSignInRequiredReason.DeviceSecretRejected, e);
                return null;
            }
        }

        private void MarkEnded(Session session, SvSignInRequiredReason reason, SvApiException e)
        {
            _lastRenewReason = reason;
            _lastRenewCode = e.ErrorCode;
            _lastRenewError = e;
            if (session == null) return;
            lock (_gate)
            {
                session.EndReason = reason;
                session.EndCode = e.ErrorCode;
            }
        }

        /// <summary>
        /// Tells the game (once per ended sign-in) and awaits <see cref="Reauthenticate"/>. Never for a request whose
        /// epoch has moved: a late 401 of a request sent before a sign-out (or another sign-in) must not ask the game to
        /// sign anyone in again.
        /// </summary>
        private async Task<string> AskGameCoreAsync(string usedToken, long epoch)
        {
            var cur = Current();
            if (cur != null && cur.Token != usedToken && cur.Token != _deadToken && !IsExpired(cur)) return cur.Token;
            if (Epoch != epoch) return null;
            if (usedToken != null && usedToken == _deadToken) return null;
            _deadToken = usedToken;
            var reason = cur?.EndReason ?? _lastRenewReason;
            var code = cur?.EndCode ?? _lastRenewCode ?? TokenExpiredCode;
            var info = new SvSignInRequired(reason, cur?.Method ?? _lastMethod, code);
            RaiseSignInRequired(info);
            var reauth = Reauthenticate;
            if (reauth == null) return null;
            bool ok;
            try
            {
                ok = await reauth(info, CancellationToken.None);
            }
            catch (Exception e)
            {
                SvLog.Write(_sv.Pipeline.Log, SvLogLevel.Warning, "Reauthenticate threw " + e.GetType().Name + "; the waiting requests fail.");
                return null;
            }
            var after = Current();
            return ok && after != null && after.Token != usedToken ? after.Token : null;
        }

        /// <summary>A 401 on a player route: the sign-in codes end the session; other codes are the route's own.</summary>
        private void OnTokenRefused(string token, SvApiException e)
        {
            var cur = Current();
            if (cur == null || cur.Token != token) return;
            if (e.Is("token_invalid"))
            {
                ClearSession(true);
                RaiseSignInRequired(new SvSignInRequired(SvSignInRequiredReason.TokenInvalid, cur.Method, e.ErrorCode));
            }
            else if (e.Is("player_deleted"))
            {
                ClearSession(true);
                _ = ForgetErasedPlayerAsync(cur.Player?.Id, true);
                RaiseSignInRequired(new SvSignInRequired(SvSignInRequiredReason.PlayerDeleted, cur.Method, e.ErrorCode));
            }
        }

        /// <summary>
        /// The player no longer exists: forget what this device kept for them, and only for them (their refresh
        /// token, their guest secret, their unanswered store changes); the next restore signs nobody in.
        /// </summary>
        /// <param name="playerId">The erased player.</param>
        /// <param name="stillTheirs">
        /// They were still the session (nobody signed in or out since): a stored refresh token or last sign-in without a
        /// player is theirs too. Otherwise only records that name them are touched.
        /// </param>
        private async Task ForgetErasedPlayerAsync(string playerId, bool stillTheirs)
        {
            RefreshRecord refresh = null;
            try
            {
                refresh = await GetRefreshRecordAsync();
            }
            catch (SvApiException)
            {
                // Unreadable: it may be someone else's; delete it only when the erased player was still the session.
                if (stillTheirs) await DeleteRefreshTokenAsync();
            }
            if (refresh != null && (refresh.PlayerId == playerId && playerId != null || refresh.PlayerId == null && stillTheirs))
                await DeleteRefreshTokenAsync();
            await ForgetDeviceIfOwnedByAsync(playerId);
            if (playerId != null) await _sv.Store.ForgetPendingForPlayerAsync(playerId);
            var last = await ReadLastSignInAsync(false);
            if (last != null && (last.PlayerId == playerId && playerId != null || last.PlayerId == null && stillTheirs))
                await WriteLastSignInAsync(null, null);
        }

        // ─── sign-in plumbing ───────────────────────────────────────────────────────────────────────────────────

        private async Task<SvSignInResult> ExchangeAsync(string provider, JObject proof, string appId, CancellationToken ct)
        {
            var body = GameBody();
            if (appId != null) body["appId"] = appId;
            foreach (var p in proof.Properties()) body[p.Name] = p.Value;
            var started = Now();
            var response = await _sv.Pipeline.SendAsync(Post("/player-auth/" + provider, body, SvRetry.Never), null, ct);
            return await AcceptAsync(SvPipeline.Read<SignInAnswer>(response), provider, false, started);
        }

        private async Task<SvPlayerProfile> LinkAsync(string provider, JObject proof, string appId, CancellationToken ct)
        {
            if (appId != null) proof["appId"] = appId;
            var response = await SendAsPlayerAsync(Post("/player-auth/link/" + provider, proof, SvRetry.Never), ct);
            return Profile(SvPipeline.Read<ProfileAnswer>(response));
        }

        private async Task<SvPlayerProfile> LinkEmailAsync(JObject body, CancellationToken ct)
        {
            var response = await SendAsPlayerAsync(Post("/player-auth/link/email", body, SvRetry.Never), ct);
            var answer = SvPipeline.Read<ProfileAnswer>(response);
            // A changed email signs out every other device and answers this device's new refresh token.
            if (!string.IsNullOrEmpty(answer.RefreshToken)) await SaveRefreshTokenAsync(answer.RefreshToken, answer.Player?.Id ?? PlayerId);
            return Profile(answer);
        }

        private async Task<SvSignInResult> CreateGuestCoreAsync(string displayName, CancellationToken ct)
        {
            var body = GameBody();
            if (displayName != null) body["displayName"] = displayName;
            var started = Now();
            var response = await _sv.Pipeline.SendAsync(Post("/player-auth/device/new", body, SvRetry.Never), null, ct);
            return await AcceptAsync(SvPipeline.Read<SignInAnswer>(response), SvProviders.Device, false, started);
        }

        /// <summary>
        /// Signs in with the guest secret. With <paramref name="renewalEpoch"/> (a renewal or a restore) the answer is
        /// accepted only when nobody signed in or out meanwhile; null then.
        /// </summary>
        private async Task<SvSignInResult> DeviceSignInCoreAsync(DeviceRecord device, CancellationToken ct, long? renewalEpoch = null)
        {
            var body = GameBody();
            body["deviceSecret"] = device.Secret;
            var started = Now();
            try
            {
                var response = await _sv.Pipeline.SendAsync(Post("/player-auth/device", body, SvRetry.Never), null, ct);
                return await AcceptAsync(SvPipeline.Read<SignInAnswer>(response), SvProviders.Device, false, started, renewalEpoch);
            }
            catch (SvApiException e) when (e.Is("proof_invalid"))
            {
                // No guest has this secret any more. Never sent again by the SDK; the secret stays stored, and the game
                // decides (ForgetGuestOnDeviceAsync / CreateGuestAsync(replaceExistingGuest: true)).
                _deviceRejected = true;
                throw;
            }
        }

        /// <summary>Refreshes; false when the answer came after a sign-in or sign-out (not accepted, its token ended).</summary>
        private async Task<bool> RefreshCoreAsync(string refreshToken, long epoch)
        {
            var call = Post("/player-auth/refresh", new JObject { ["refreshToken"] = refreshToken }, SvRetry.Safe);
            // The token is spent when the API reads it, and a lost answer may be resent with the same token only
            // within 30 seconds: short sends (8 s) and no new send after 25 s keep every resend inside that grace.
            call.RetryWindow = TimeSpan.FromSeconds(25);
            call.Timeout = _refreshTimeout;
            var started = Now();
            var response = await _sv.Pipeline.SendAsync(call, null, CancellationToken.None);
            return await AcceptAsync(SvPipeline.Read<SignInAnswer>(response), SvProviders.Email, true, started, epoch) != null;
        }

        /// <summary>
        /// Takes a sign-in answer: the refresh token and the device secret are saved BEFORE the new player token is
        /// used; a sign-in without a refresh token ends the stored email sign-in (another way in replaced it). The
        /// token's expiry is counted on this device's clock from <paramref name="started"/>.
        /// </summary>
        /// <param name="renewalEpoch">
        /// A renewal or restore: the epoch it started under. The answer is accepted only when it still holds (nobody
        /// signed in or out meanwhile); otherwise nothing is kept, its refresh token is signed out on the server, and
        /// null is returned. Null for a sign-in the game asked for (it starts a new epoch).
        /// </param>
        private async Task<SvSignInResult> AcceptAsync(SignInAnswer answer, string method, bool fromRefresh, Stamp started,
            long? renewalEpoch = null)
        {
            if (string.IsNullOrEmpty(answer.Token) || answer.Player == null)
                throw new SvApiException(200, SvApiException.InvalidResponseCode, "The sign-in answer had no token.");
            if (renewalEpoch.HasValue && Epoch != renewalEpoch.Value)
            {
                await DiscardLateAnswerAsync(answer, false);
                return null;
            }
            // A sign-in the game asked for starts its epoch now, before anything is saved: a renewal or restore that
            // lands while this saves is then already stale and cannot overwrite it.
            var epoch = renewalEpoch ?? NewEpoch();
            var saved = true;
            if (!string.IsNullOrEmpty(answer.RefreshToken)) saved &= await SaveRefreshTokenAsync(answer.RefreshToken, answer.Player.Id);
            else if (!fromRefresh) await DropRefreshTokenAsync(true);
            if (!string.IsNullOrEmpty(answer.DeviceSecret)) saved &= await SaveDeviceAsync(answer.DeviceSecret, answer.Player.Id);
            else if (method == SvProviders.Device) await RecordDeviceOwnerAsync(answer.Player.Id);
            if (method == SvProviders.Device) _deviceRejected = false;

            var lifetime = Lifetime(answer.Token, answer.ExpiresAt);
            var valid = lifetime - ClockMargin;
            // Renew ahead of expiry, but never more often than every half lifetime (a short-lived token cannot make
            // every request a refresh).
            var lead = _renewLead < TimeSpan.FromTicks(lifetime.Ticks / 2) ? _renewLead : TimeSpan.FromTicks(lifetime.Ticks / 2);
            var session = new Session
            {
                Token = answer.Token,
                ExpiresAt = answer.ExpiresAt,
                Expiry = started.Wall + valid,
                RenewAt = started.Wall + valid - lead,
                MonoExpiry = started.Mono + valid,
                MonoRenewAt = started.Mono + valid - lead,
                Method = method,
                Player = answer.Player,
                Identities = answer.Identities ?? new List<SvIdentity>()
            };
            if (!TrySetSession(session, epoch))
            {
                // Signed in or out while the answer was being saved: undo what it saved.
                await DiscardLateAnswerAsync(answer, true);
                if (renewalEpoch.HasValue) return null;
                throw Superseded();
            }
            await WriteLastSignInAsync(method, answer.Player.Id);
            var result = ToResult(session, IsRenewableNow(session), saved);
            RaiseSignedIn(result);
            return result;
        }

        /// <summary>
        /// The token's lifetime: its <c>exp</c> (or the answer's expiresAt) minus its <c>iat</c>, kept within 60 s to
        /// 24 h; one hour when they are missing, unreadable, out of range or not increasing. Never throws (it runs
        /// after secrets were saved).
        /// </summary>
        private static TimeSpan Lifetime(string token, string expiresAt)
        {
            try
            {
                var claims = ReadClaims(token);
                var iat = FromUnix(Seconds(claims?["iat"]));
                var end = FromUnix(Seconds(claims?["exp"])) ?? SvDates.Parse(expiresAt);
                if (iat.HasValue && end.HasValue)
                {
                    var life = end.Value - iat.Value;
                    if (life > TimeSpan.Zero) return life < MinLifetime ? MinLifetime : life > MaxLifetime ? MaxLifetime : life;
                }
            }
            catch (Exception)
            {
                // Anything odd in a token: the documented default.
            }
            return DefaultLifetime;
        }

        private SvPlayerProfile Profile(ProfileAnswer answer)
        {
            var profile = new SvPlayerProfile
            {
                Player = answer.Player,
                Game = answer.Game,
                Identities = answer.Identities ?? new List<SvIdentity>()
            };
            lock (_gate)
            {
                if (_session != null && answer.Player != null && (_session.Player?.Id == null || _session.Player.Id == answer.Player.Id))
                {
                    _session.Identities = profile.Identities;
                    if (_session.Player == null) _session.Player = new SvSignedInPlayer();
                    _session.Player.Id = answer.Player.Id;
                    _session.Player.DisplayName = answer.Player.DisplayName;
                    if (answer.Player.CreatedAt != null) _session.Player.CreatedAt = answer.Player.CreatedAt;
                }
            }
            return profile;
        }

        private static SvSignInResult ToResult(Session s, bool renewable, bool saved)
        {
            return new SvSignInResult
            {
                Method = s?.Method,
                ExpiresAt = s?.ExpiresAt,
                Player = s?.Player,
                Identities = s?.Identities ?? new List<SvIdentity>(),
                Renewable = renewable,
                SavedToDevice = saved
            };
        }

        // ─── session ────────────────────────────────────────────────────────────────────────────────────────────

        private Session Current()
        {
            lock (_gate) return _session;
        }

        private bool IsExpired(Session s)
        {
            return IsDue(s.Expiry, s.MonoExpiry);
        }

        /// <summary>
        /// Due on EITHER clock: the monotonic one (a time sync backwards cannot delay it) or the wall clock (it keeps
        /// counting while the device sleeps, when some monotonic clocks stop).
        /// </summary>
        private bool IsDue(DateTimeOffset? wall, TimeSpan? mono)
        {
            return (wall.HasValue && _sv.Pipeline.Now >= wall.Value) || (mono.HasValue && _sv.Pipeline.Mono >= mono.Value);
        }

        private Stamp Now()
        {
            return new Stamp { Wall = _sv.Pipeline.Now, Mono = _sv.Pipeline.Mono };
        }

        /// <summary>Renewable after a restart too: a saved refresh token, or (a guest) its saved device secret.</summary>
        private bool IsRenewableNow(Session s)
        {
            if (s == null) return false;
            lock (_gate)
            {
                if (_refresh != null && !_refreshUnsaved && _refresh.PlayerId != null && _refresh.PlayerId == s.Player?.Id) return true;
                return s.Method == SvProviders.Device && _device != null && !_deviceUnsaved &&
                       (_device.PlayerId == null || s.Player?.Id == null || _device.PlayerId == s.Player.Id);
            }
        }

        /// <summary>The SDK can renew this session without the player (in memory counts). Storage errors: no.</summary>
        private async Task<bool> IsRenewableQuietAsync(Session s)
        {
            try
            {
                if (await UsableRefreshTokenAsync(s, null) != null) return true;
                return s.Method == SvProviders.Device && !_deviceRejected && await GetDeviceAsync() != null;
            }
            catch (SvApiException)
            {
                return false;
            }
        }

        private long Epoch
        {
            get
            {
                lock (_gate) return _epoch;
            }
        }

        private void BumpEpoch()
        {
            lock (_gate) _epoch++;
        }

        /// <summary>Starts a new epoch (a sign-in the game asked for) and returns it.</summary>
        private long NewEpoch()
        {
            lock (_gate) return ++_epoch;
        }

        /// <summary>A sign-in the game asked for was overtaken by a later sign-in or a sign-out while it was saved.</summary>
        private static SvApiException Superseded()
        {
            return new SvApiException(0, SvApiException.SupersededCode, null);
        }

        /// <summary>
        /// Sets the session only while <paramref name="epoch"/> still holds (a renewal keeps its epoch; a game sign-in
        /// started a new one before saving anything). False when a sign-in or sign-out came first.
        /// </summary>
        private bool TrySetSession(Session s, long epoch)
        {
            lock (_gate)
            {
                if (_epoch != epoch) return false;
                _session = s;
                _lastMethod = s.Method;
                _deadToken = null;
                return true;
            }
        }

        /// <summary>
        /// A renewal or restore answered after the game signed in or out: nothing of it is kept, and its new refresh token
        /// (the only live one of that sign-in) is signed out on the server, quietly.
        /// </summary>
        private async Task DiscardLateAnswerAsync(SignInAnswer answer, bool saved)
        {
            SvLog.Write(_sv.Pipeline.Log, SvLogLevel.Info, "A renewal answered after a sign-in or sign-out was not used.");
            if (string.IsNullOrEmpty(answer.RefreshToken)) return;
            if (saved) await DeleteRefreshTokenIfAsync(answer.RefreshToken);
            await SignOutQuietlyAsync(answer.RefreshToken);
        }

        private void ClearSession(bool raise)
        {
            bool had;
            lock (_gate)
            {
                had = _session != null;
                _session = null;
                _epoch++;
            }
            if (raise && had)
            {
                try
                {
                    SignedOut?.Invoke();
                }
                catch (Exception e)
                {
                    SvLog.Write(_sv.Pipeline.Log, SvLogLevel.Error, "A SignedOut handler threw " + e.GetType().Name + ".");
                }
            }
        }

        private void RaiseSignedIn(SvSignInResult result)
        {
            try
            {
                SignedIn?.Invoke(result);
            }
            catch (Exception e)
            {
                SvLog.Write(_sv.Pipeline.Log, SvLogLevel.Error, "A SignedIn handler threw " + e.GetType().Name + ".");
            }
        }

        private void RaiseSignInRequired(SvSignInRequired info)
        {
            try
            {
                SignInRequired?.Invoke(info);
            }
            catch (Exception e)
            {
                SvLog.Write(_sv.Pipeline.Log, SvLogLevel.Error, "A SignInRequired handler threw " + e.GetType().Name + ".");
            }
        }

        internal void RaiseStorageFailed(string item, SvStorageOperation operation, Exception error)
        {
            SvLog.Write(_sv.Pipeline.Log, operation == SvStorageOperation.Read ? SvLogLevel.Warning : SvLogLevel.Error,
                "Secure storage: " + operation.ToString().ToLowerInvariant() + " of " + item + " failed (" + error.GetType().Name + ").");
            try
            {
                StorageFailed?.Invoke(new SvStorageFailure(item, operation, error));
            }
            catch (Exception e)
            {
                SvLog.Write(_sv.Pipeline.Log, SvLogLevel.Error, "A StorageFailed handler threw " + e.GetType().Name + ".");
            }
        }

        // ─── storage ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A stored value; null only when there is certainly none. Unreadable: <c>storage_unreadable</c>, nothing cached.</summary>
        private async Task<string> ReadAsync(string key, string item)
        {
            try
            {
                var value = await _sv.SecureStore.GetAsync(key);
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (Exception e)
            {
                RaiseStorageFailed(item, SvStorageOperation.Read, e);
                throw Unreadable(item, e);
            }
        }

        private async Task<bool> WriteKeyAsync(string key, string item, string value)
        {
            try
            {
                await _sv.SecureStore.SetAsync(key, value);
                return true;
            }
            catch (Exception e)
            {
                RaiseStorageFailed(item, SvStorageOperation.Write, e);
                return false;
            }
        }

        private async Task<bool> DeleteKeyAsync(string key, string item)
        {
            try
            {
                await _sv.SecureStore.DeleteAsync(key);
                return true;
            }
            catch (Exception e)
            {
                RaiseStorageFailed(item, SvStorageOperation.Delete, e);
                return false;
            }
        }

        internal static SvApiException Unreadable(string item, Exception e)
        {
            var what = item == GuestItem ? "guest secret" : item == RefreshItem ? "sign-in" : item.Replace('_', ' ');
            return new SvApiException(0, SvApiException.StorageUnreadableCode,
                "The " + what + " stored on this device could not be read. Nothing was changed; try again.", inner: e);
        }

        /// <summary>The next read of the refresh token goes to storage (unless it is only in memory: not saved).</summary>
        private void ForgetCachedRefreshToken()
        {
            lock (_gate)
                if (!_refreshUnsaved) _refreshLoaded = false;
        }

        private async Task<RefreshRecord> GetRefreshRecordAsync()
        {
            lock (_gate)
                if (_refreshLoaded) return _refresh;
            var stored = await ReadAsync(RefreshKey, RefreshItem);
            RefreshRecord record;
            try
            {
                record = ParseRefresh(stored);
            }
            catch (Exception e)
            {
                RaiseStorageFailed(RefreshItem, SvStorageOperation.Read, e);
                throw Unreadable(RefreshItem, e);
            }
            lock (_gate)
            {
                if (!_refreshLoaded)
                {
                    _refresh = record;
                    _refreshLoaded = true;
                }
                return _refresh;
            }
        }

        private static RefreshRecord ParseRefresh(string stored)
        {
            if (stored == null) return null;
            // A bare token (no player): kept readable, never used (it cannot be tied to the signed-in player).
            if (stored.StartsWith("svpr1_", StringComparison.Ordinal)) return new RefreshRecord { Token = stored };
            if (SvJson.Parse(stored) is JObject o && o["token"] is JValue token && token.Type == JTokenType.String &&
                !string.IsNullOrEmpty((string)token))
            {
                var owner = o["player"] is JValue p && p.Type == JTokenType.String ? (string)p : null;
                return new RefreshRecord { Token = (string)token, PlayerId = owner };
            }
            throw new SvSecureStoreException("The stored refresh token is not in a known format.");
        }

        /// <summary>
        /// The stored refresh token when it is THIS player's: the signed-in player's (<paramref name="session"/>), or
        /// with no session the player of the last sign-in when that was an email sign-in. Anything else is a
        /// leftover (another player's, one whose delete failed, one without a player): never used, and deleted
        /// again. Unreadable storage throws <c>storage_unreadable</c>.
        /// </summary>
        private async Task<string> UsableRefreshTokenAsync(Session session, LastSignIn last, long? epoch = null)
        {
            var record = await GetRefreshRecordAsync();
            if (record == null) return null;
            // Who the token must belong to is decided by the session the caller saw: once a sign-in or sign-out moved
            // the epoch, the stored token may be the new player's, never a leftover to delete.
            if (epoch.HasValue && Epoch != epoch.Value) return null;
            string expected;
            if (session != null)
            {
                expected = session.Player?.Id;
            }
            else
            {
                if (last == null) last = await ReadLastSignInAsync(true);
                expected = last != null && last.Method == SvProviders.Email ? last.PlayerId : null;
            }
            if (expected != null && record.PlayerId == expected) return record.Token;
            if (epoch.HasValue && Epoch != epoch.Value) return null;
            SvLog.Write(_sv.Pipeline.Log, SvLogLevel.Info, "A stored refresh token of another player (or of none) was not used, and is deleted.");
            await DeleteRefreshTokenIfAsync(record.Token);
            return null;
        }

        private static string RefreshJson(RefreshRecord record)
        {
            var o = new JObject { ["v"] = 1, ["token"] = record.Token };
            if (record.PlayerId != null) o["player"] = record.PlayerId;
            return SvJson.Serialize(o);
        }

        private async Task<bool> SaveRefreshTokenAsync(string token, string playerId)
        {
            var record = new RefreshRecord { Token = token, PlayerId = playerId };
            lock (_gate)
            {
                _refresh = record;
                _refreshLoaded = true;
            }
            var ok = await WriteKeyAsync(RefreshKey, RefreshItem, RefreshJson(record));
            lock (_gate)
                if (_refresh == record) _refreshUnsaved = !ok;
            if (!ok)
                SvLog.Write(_sv.Pipeline.Log, SvLogLevel.Error,
                    "The refresh token could not be saved: it is kept in memory, and the player signs in again after a restart.");
            return ok;
        }

        private async Task DeleteRefreshTokenAsync()
        {
            lock (_gate)
            {
                _refresh = null;
                _refreshLoaded = true;
                _refreshUnsaved = false;
            }
            if (!await DeleteKeyAsync(RefreshKey, RefreshItem))
                lock (_gate) _refreshLoaded = false; // still stored: read (and checked) again next time
        }

        /// <summary>
        /// Deletes the refresh token only when it is still the one that was sent: a newer one (another refresh, a new
        /// sign-in, a changed password) may have been saved meanwhile.
        /// </summary>
        private async Task DeleteRefreshTokenIfAsync(string sent)
        {
            RefreshRecord stored = null;
            var storedKnown = true;
            try
            {
                stored = ParseRefresh(await _sv.SecureStore.GetAsync(RefreshKey));
            }
            catch (Exception)
            {
                storedKnown = false;
            }
            lock (_gate)
            {
                if (_refreshLoaded && _refresh != null && _refresh.Token != sent) return;
                if (storedKnown && stored != null && stored.Token != sent)
                {
                    _refresh = stored;
                    _refreshLoaded = true;
                    return;
                }
            }
            await DeleteRefreshTokenAsync();
        }

        /// <summary>Forgets the stored email sign-in, and ends it on the server in the background.</summary>
        private async Task DropRefreshTokenAsync(bool signOutOnServer)
        {
            string old;
            try
            {
                old = (await GetRefreshRecordAsync())?.Token;
            }
            catch (SvApiException)
            {
                await DeleteRefreshTokenAsync();
                return;
            }
            if (old == null) return;
            await DeleteRefreshTokenAsync();
            if (!signOutOnServer) return;
            _ = SignOutQuietlyAsync(old);
        }

        private async Task SignOutQuietlyAsync(string refreshToken)
        {
            try
            {
                await _sv.Pipeline.SendAsync(Post("/player-auth/sign-out", new JObject { ["refreshToken"] = refreshToken }, SvRetry.Never), null,
                    CancellationToken.None);
            }
            catch (Exception)
            {
                // Best effort: nobody holds that refresh token any more.
            }
        }

        private async Task<DeviceRecord> GetDeviceAsync()
        {
            lock (_gate)
                if (_deviceLoaded) return _device;
            var stored = await ReadAsync(DeviceKey, GuestItem);
            DeviceRecord record;
            try
            {
                record = ParseDevice(stored);
            }
            catch (Exception e)
            {
                RaiseStorageFailed(GuestItem, SvStorageOperation.Read, e);
                throw Unreadable(GuestItem, e);
            }
            lock (_gate)
            {
                if (!_deviceLoaded)
                {
                    _device = record;
                    _deviceLoaded = true;
                }
                return _device;
            }
        }

        private static DeviceRecord ParseDevice(string stored)
        {
            if (stored == null) return null;
            if (stored.StartsWith("svpd1_", StringComparison.Ordinal)) return new DeviceRecord { Secret = stored };
            if (SvJson.Parse(stored) is JObject o && o["secret"] is JValue secret && secret.Type == JTokenType.String &&
                !string.IsNullOrEmpty((string)secret))
            {
                var owner = o["player"] is JValue p && p.Type == JTokenType.String ? (string)p : null;
                return new DeviceRecord { Secret = (string)secret, PlayerId = owner };
            }
            throw new SvSecureStoreException("The stored guest secret is not in a known format.");
        }

        private static string DeviceJson(DeviceRecord record)
        {
            var o = new JObject { ["v"] = 1, ["secret"] = record.Secret };
            if (record.PlayerId != null) o["player"] = record.PlayerId;
            return SvJson.Serialize(o);
        }

        /// <summary>Saves a guest secret with the player it belongs to, and reads it back. False when it is not safely stored.</summary>
        private async Task<bool> SaveDeviceAsync(string secret, string playerId)
        {
            var record = new DeviceRecord { Secret = secret, PlayerId = playerId };
            lock (_gate)
            {
                _device = record;
                _deviceLoaded = true;
            }
            var json = DeviceJson(record);
            var ok = await WriteKeyAsync(DeviceKey, GuestItem, json);
            if (ok)
            {
                try
                {
                    var back = await _sv.SecureStore.GetAsync(DeviceKey);
                    if (back != json)
                    {
                        ok = false;
                        RaiseStorageFailed(GuestItem, SvStorageOperation.Write, new SvSecureStoreException("The saved guest secret does not read back."));
                    }
                }
                catch (Exception e)
                {
                    ok = false;
                    RaiseStorageFailed(GuestItem, SvStorageOperation.Read, e);
                }
            }
            lock (_gate)
                if (_device == record) _deviceUnsaved = !ok;
            if (!ok)
                SvLog.Write(_sv.Pipeline.Log, SvLogLevel.Error,
                    "The guest secret could not be saved: it is kept in memory, and the guest is lost when the game quits unless the player adds an email.");
            return ok;
        }

        /// <summary>A guest secret stored without its player gets the player (best effort).</summary>
        private async Task RecordDeviceOwnerAsync(string playerId)
        {
            DeviceRecord current;
            lock (_gate) current = _deviceLoaded ? _device : null;
            if (current == null || current.PlayerId != null || playerId == null || _deviceUnsaved) return;
            current.PlayerId = playerId;
            await WriteKeyAsync(DeviceKey, GuestItem, DeviceJson(current));
        }

        /// <summary>Forgets this device's guest secret only when it belongs to <paramref name="playerId"/>.</summary>
        private async Task ForgetDeviceIfOwnedByAsync(string playerId)
        {
            if (playerId == null) return;
            DeviceRecord record;
            try
            {
                record = await GetDeviceAsync();
            }
            catch (SvApiException)
            {
                return; // unreadable: it may be another player's; keep it
            }
            if (record == null || record.PlayerId == null || record.PlayerId != playerId) return;
            await ForgetGuestOnDeviceAsync();
        }

        /// <summary>
        /// The last sign-in record; null when there is none. When it cannot be read (or parsed): with
        /// <paramref name="throwIfUnreadable"/> <c>storage_unreadable</c>, else null.
        /// </summary>
        private async Task<LastSignIn> ReadLastSignInAsync(bool throwIfUnreadable)
        {
            string stored;
            try
            {
                stored = await _sv.SecureStore.GetAsync(LastKey);
            }
            catch (Exception e)
            {
                RaiseStorageFailed(LastItem, SvStorageOperation.Read, e);
                if (throwIfUnreadable) throw Unreadable(LastItem, e);
                return null;
            }
            if (string.IsNullOrEmpty(stored)) return null;
            try
            {
                if (!(SvJson.Parse(stored) is JObject o)) throw new SvSecureStoreException("The last sign-in is not in a known format.");
                var method = o["method"] is JValue m && m.Type == JTokenType.String ? (string)m : null;
                var player = o["player"] is JValue p && p.Type == JTokenType.String ? (string)p : null;
                return new LastSignIn { Method = method, PlayerId = player };
            }
            catch (Exception e)
            {
                RaiseStorageFailed(LastItem, SvStorageOperation.Read, e);
                if (throwIfUnreadable) throw Unreadable(LastItem, e);
                return null;
            }
        }

        /// <summary>
        /// Records how the player signed in (null: signed out), for <see cref="RestoreAsync"/>. Not written again when the
        /// store already holds exactly that (every renewal and restore would otherwise spend a sealed write on it); the
        /// check is a read, which the file stores answer from memory unless another process changed the value.
        /// </summary>
        private async Task<bool> WriteLastSignInAsync(string method, string playerId)
        {
            var o = new JObject { ["method"] = method, ["player"] = playerId };
            var json = SvJson.Serialize(o);
            try
            {
                if (await _sv.SecureStore.GetAsync(LastKey) == json) return true;
            }
            catch (Exception)
            {
                // Unreadable: write it (as always), which reports a failure through StorageFailed.
            }
            return await WriteKeyAsync(LastKey, LastItem, json);
        }

        // ─── small helpers ──────────────────────────────────────────────────────────────────────────────────────

        private JObject GameBody()
        {
            var body = new JObject();
            _sv.AddGame(body);
            return body;
        }

        private static SvCall Post(string path, JObject body, SvRetry retry)
        {
            return new SvCall { Method = "POST", Path = path, Body = body, Retry = retry };
        }

        private static string Required(string value, string name)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException(name + " is required.", name);
            return value;
        }

        private static JObject MetaProof(string userId, string nonce)
        {
            Required(userId, nameof(userId));
            if (userId.Length > 20) throw new ArgumentException("userId must be the Meta user id: 1 to 20 digits.", nameof(userId));
            foreach (var c in userId)
                if (c < '0' || c > '9')
                    throw new ArgumentException("userId must be the Meta user id: 1 to 20 digits.", nameof(userId));
            return new JObject { ["userId"] = userId, ["nonce"] = Required(nonce, nameof(nonce)) };
        }

        private static JObject SteamProof(string ticketHex)
        {
            Required(ticketHex, nameof(ticketHex));
            foreach (var c in ticketHex)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    throw new ArgumentException("ticketHex must be the Steam ticket as hex.", nameof(ticketHex));
            return new JObject { ["ticket"] = ticketHex };
        }

        private static double? Seconds(JToken token)
        {
            try
            {
                if (token is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float)) return (double)v;
            }
            catch (Exception)
            {
                // A number no double holds: treated as missing.
            }
            return null;
        }

        /// <summary>Unix seconds as a time; null when missing, not finite, or outside 1970-9999.</summary>
        private static DateTimeOffset? FromUnix(double? seconds)
        {
            if (!seconds.HasValue) return null;
            var v = seconds.Value;
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0 || v > 253402300799d) return null;
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(v * 1000));
        }

        private static DateTimeOffset? ParseHttpDate(string header)
        {
            if (string.IsNullOrEmpty(header)) return null;
            return DateTimeOffset.TryParseExact(header.Trim(), "r", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                ? at
                : (DateTimeOffset?)null;
        }

        /// <summary>The token's claims, read without checking the signature (only for its lifetime and method).</summary>
        private static JObject ReadClaims(string jwt)
        {
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length != 3) return null;
                var b64 = parts[1].Replace('-', '+').Replace('_', '/');
                switch (b64.Length % 4)
                {
                    case 2: b64 += "=="; break;
                    case 3: b64 += "="; break;
                }
                return SvJson.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))) as JObject;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
