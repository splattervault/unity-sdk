using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SplatterVault.Players;

namespace SplatterVault.Core.Tests
{
    /// <summary>
    /// Rule 11 of the cross-SDK rules: a renewal (refresh, guest renew) or a restore answered after the session changed
    /// (signed out, another player signed in) is not accepted, and its new refresh token is signed out on the server.
    /// Plus: two clients sharing one store re-read the refresh token before each refresh (Unreal review finding 2).
    /// </summary>
    [TestClass]
    public class AuthEpochTests
    {
        private const string EmailPlayer = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b";
        private const string SteamPlayer = "bbbbbbbb-0000-4000-8000-000000000002";

        private static List<string> SignedOutTokens(Kit kit) =>
            kit.Transport.To("POST", "/player-auth/sign-out").Select(r => (string)Kit.Body(r)["refreshToken"]).ToList();

        [TestMethod]
        public async Task SignOut_DuringARefresh_IsNotUndoneByTheLateAnswer()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync("t1", "svpr1_A");
            var events = new List<string>();
            kit.Sv.Auth.SignedIn += _ => events.Add("signed in");
            kit.Sv.Auth.SignedOut += () => events.Add("signed out");
            kit.Transport.Handler = async r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/sign-out") return Http.Json(200, new JObject());
                if (path == "/player-auth/refresh")
                {
                    await kit.Sv.Auth.SignOutAsync(); // the player signs out while the refresh is in flight
                    return Http.Json(200, Http.SignIn("t2", refresh: "svpr1_B"));
                }
                return r.Headers["Authorization"] == "Bearer t2" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired");
            };
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());

            Assert.IsFalse(kit.Sv.Auth.IsSignedIn, "the late refresh answer resurrected a signed-out player");
            Assert.IsNull(kit.Sv.Auth.PlayerToken);
            Assert.IsNull(kit.Recording.RefreshToken(), "the late refresh token was stored");
            CollectionAssert.Contains(SignedOutTokens(kit), "svpr1_B", "the late refresh token is signed out on the server");
            CollectionAssert.AreEqual(new[] { "signed out" }, events);
            Assert.IsFalse(await kit.Sv.Auth.RestoreAsync(), "a restore after sign-out signs nobody in");
        }

        [TestMethod]
        public async Task RestoreRefresh_LandingAfterAPlatformSignIn_DoesNotReplaceIt()
        {
            var email = new Kit();
            await email.SignInByEmailAsync("t1", "svpr1_A");
            var kit = new Kit(store: email.Store); // next launch, same storage
            kit.Transport.Handler = async r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/sign-out") return Http.Json(200, new JObject());
                if (path == "/player-auth/steam") return Http.Json(200, Http.SignIn("tS", provider: "steam", playerId: SteamPlayer));
                if (path == "/player-auth/refresh")
                {
                    // The game signs a Steam player in while the startup restore's refresh is in flight.
                    await kit.Sv.Auth.SignInWithSteamAsync("AB");
                    return Http.Json(200, Http.SignIn("tE", refresh: "svpr1_B"));
                }
                return Http.Error(404, "not_found");
            };
            var restored = await kit.Sv.Auth.RestoreAsync();

            Assert.IsFalse(restored);
            Assert.AreEqual(SteamPlayer, kit.Sv.Auth.PlayerId, "the late restore replaced the newer sign-in");
            Assert.AreEqual("tS", kit.Sv.Auth.PlayerToken);
            Assert.AreEqual("steam", await kit.Sv.Auth.GetLastSignInMethodAsync());
            Assert.IsNull(kit.Recording.RefreshToken());
            CollectionAssert.Contains(SignedOutTokens(kit), "svpr1_B");
        }

        [TestMethod]
        public async Task GuestRenew_LandingAfterAnotherSignIn_DoesNotReplaceIt()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("tG", device: "svpd1_G", provider: "device")));
            await kit.Sv.Auth.SignInAsGuestAsync();
            Assert.AreEqual("device", kit.Sv.Auth.Method);
            kit.Transport.Handler = async r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/steam") return Http.Json(200, Http.SignIn("tS", provider: "steam", playerId: SteamPlayer));
                if (path == "/player-auth/device")
                {
                    await kit.Sv.Auth.SignInWithSteamAsync("AB");
                    return Http.Json(200, Http.SignIn("tG2", provider: "device"));
                }
                return r.Headers["Authorization"] == "Bearer tS" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired");
            };
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(SteamPlayer, kit.Sv.Auth.PlayerId, "the late guest renewal replaced the Steam session");
            Assert.AreEqual("tS", kit.Sv.Auth.PlayerToken);
            Assert.AreEqual("svpd1_G", kit.Recording.GuestSecret(), "the guest secret stays on the device");
        }

        [TestMethod]
        public async Task AWriteMadeForOnePlayer_IsNeverSentWithAnotherPlayersToken()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync("t1", "svpr1_A");
            kit.Transport.Handler = async r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/sign-out") return Http.Json(200, new JObject());
                if (path == "/player-auth/steam") return Http.Json(200, Http.SignIn("tS", provider: "steam", playerId: SteamPlayer));
                if (path == "/player-auth/refresh")
                {
                    await kit.Sv.Auth.SignInWithSteamAsync("AB"); // B signs in while A's token is being renewed
                    return Http.Json(200, Http.SignIn("t2", refresh: "svpr1_B"));
                }
                return r.Headers["Authorization"] == "Bearer t1"
                    ? Http.Error(401, "token_expired", "Token expired")
                    : Http.Json(200, JObject.Parse("{\"results\":[{\"store\":\"player\",\"key\":\"wins\",\"version\":2}]}"));
            };
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Data.WriteAsync(new SvDataOps().Set("wins", 5, ifVersion: 1)));
            var writes = kit.Transport.To("PATCH", "/player-data/me");
            Assert.IsTrue(writes.All(w => w.Headers["Authorization"] == "Bearer t1"),
                "A's write went out as: " + string.Join(",", writes.Select(w => w.Headers["Authorization"])));
            Assert.AreEqual(SteamPlayer, kit.Sv.Auth.PlayerId);
        }

        /// <summary>A memory store that runs a hook once, after a write or a delete of the item with this suffix.</summary>
        private sealed class HookStore : ISecureStore
        {
            private readonly SvMemorySecureStore _inner = new SvMemorySecureStore();
            public string OnSet;
            public string OnDelete;
            public Func<Task> Hook;

            public Task<string> GetAsync(string key) => _inner.GetAsync(key);

            public async Task SetAsync(string key, string value)
            {
                await _inner.SetAsync(key, value);
                if (OnSet != null && key.EndsWith(OnSet)) await RunHook();
            }

            public async Task DeleteAsync(string key)
            {
                await _inner.DeleteAsync(key);
                if (OnDelete != null && key.EndsWith(OnDelete)) await RunHook();
            }

            private async Task RunHook()
            {
                var hook = Hook;
                Hook = null;
                if (hook != null) await hook();
            }
        }

        [TestMethod]
        public async Task RefreshAnswered_WhileSignOutRuns_IsNotKept()
        {
            var store = new HookStore();
            var kit = new Kit(store: store);
            await kit.SignInByEmailAsync("t1", "svpr1_A");
            var gate = new TaskCompletionSource<SvHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            kit.Transport.Handler = r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/sign-out") return Http.Done(Http.Json(200, new JObject()));
                if (path == "/player-auth/refresh") return gate.Task;
                return Http.Done(r.Headers["Authorization"] == "Bearer t2" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired"));
            };
            var call = kit.Sv.Store.GetMineAsync();              // its refresh is now in flight
            // The refresh answer lands while sign-out is deleting the stored token (before it clears the session).
            store.OnDelete = "refresh_token";
            store.Hook = async () =>
            {
                gate.SetResult(Http.Json(200, Http.SignIn("t2", refresh: "svpr1_B")));
                try
                {
                    await call;
                }
                catch (SvApiException)
                {
                }
            };
            await kit.Sv.Auth.SignOutAsync();

            Assert.IsTrue(call.IsFaulted, "the call made before sign-out was answered with the late renewal");
            Assert.IsFalse(kit.Sv.Auth.IsSignedIn);
            Assert.IsNull(await store.GetAsync(kit.Sv.StoragePrefix + "refresh_token"), "the late refresh token was kept");
            CollectionAssert.Contains(SignedOutTokens(kit), "svpr1_B");
        }

        [TestMethod]
        public async Task SignOut_WhileTheRefreshAnswerIsBeingSaved_Wins()
        {
            var store = new HookStore();
            var kit = new Kit(store: store);
            await kit.SignInByEmailAsync("t1", "svpr1_A");
            kit.Transport.Handler = r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/sign-out") return Http.Done(Http.Json(200, new JObject()));
                if (path == "/player-auth/refresh") return Http.Done(Http.Json(200, Http.SignIn("t2", refresh: "svpr1_B")));
                return Http.Done(r.Headers["Authorization"] == "Bearer t2" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired"));
            };
            // The player signs out right after the new refresh token was written, before the session is set.
            store.OnSet = "refresh_token";
            store.Hook = () => kit.Sv.Auth.SignOutAsync();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());

            Assert.IsFalse(kit.Sv.Auth.IsSignedIn, "the renewal set the session after the sign-out");
            Assert.IsNull(await store.GetAsync(kit.Sv.StoragePrefix + "refresh_token"));
            CollectionAssert.Contains(SignedOutTokens(kit), "svpr1_B");
        }

        [TestMethod]
        public async Task TwoClientsSharingOneStore_RefreshWithTheLatestStoredToken()
        {
            var one = new Kit();
            await one.SignInByEmailAsync("t1", "svpr1_A");
            // A second client on the same machine and store (two standalone players, Multiplayer Play Mode) restores
            // and rotates the refresh token.
            var two = new Kit(store: one.Store);
            two.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t2", refresh: "svpr1_B")));
            Assert.IsTrue(await two.Sv.Auth.RestoreAsync());
            Assert.AreEqual("svpr1_B", one.Recording.RefreshToken());

            one.Transport.Handler = r =>
            {
                if (FakeTransport.PathOf(r) == "/player-auth/refresh")
                    return Http.Done((string)Kit.Body(r)["refreshToken"] == "svpr1_B"
                        ? Http.Json(200, Http.SignIn("t3", refresh: "svpr1_C"))
                        : Http.Error(401, "refresh_invalid"));
                return Http.Done(r.Headers["Authorization"] == "Bearer t3" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired"));
            };
            await one.Sv.Store.GetMineAsync();
            Assert.AreEqual("svpr1_B", (string)Kit.Body(one.Transport.To("POST", "/player-auth/refresh").Single())["refreshToken"],
                "client one sent the token client two had already spent");
            Assert.AreEqual("svpr1_C", one.Recording.RefreshToken());
        }
    }
}
