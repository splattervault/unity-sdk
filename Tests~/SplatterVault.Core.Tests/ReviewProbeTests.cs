// Regression tests from the independent review of the Unity 4.0 RC (2026-11-15, review-unity-40p/probe/AuthProbes.cs).
// Probe bodies as written by the reviewer; names describe the fixed behaviour; P4 asserts rule 16 (IsPending).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SplatterVault.Players;

namespace SplatterVault.Core.Tests
{
    [TestClass]
    public class AuthProbes
    {
        private const string PlayerB = "bbbbbbbb-0000-4000-8000-00000000000b";

        private sealed class HookStore : ISecureStore
        {
            private readonly SvMemorySecureStore _inner = new SvMemorySecureStore();
            public string OnSet;
            public Func<Task> Hook;
            public Task<string> GetAsync(string key) => _inner.GetAsync(key);
            public async Task SetAsync(string key, string value)
            {
                await _inner.SetAsync(key, value);
                if (OnSet != null && key.EndsWith(OnSet))
                {
                    var hook = Hook;
                    Hook = null;
                    if (hook != null) await hook();
                }
            }
            public Task DeleteAsync(string key) => _inner.DeleteAsync(key);
        }

        private static List<string> RefreshesSent(Kit kit) =>
            kit.Transport.To("POST", "/player-auth/refresh").Select(r => (string)Kit.Body(r)["refreshToken"]).ToList();

        private static List<string> SignedOut(Kit kit) =>
            kit.Transport.To("POST", "/player-auth/sign-out").Select(r => (string)Kit.Body(r)["refreshToken"]).ToList();

        /// <summary>P2a: a refresh answered refresh_invalid after another player's email sign-in; the second attempt deletes B's token.</summary>
        [TestMethod]
        public async Task P2a_SecondRefreshAttempt_KeepsTheNewPlayersRefreshToken()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync("t1", "svpr1_A");
            kit.Transport.Handler = async r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/sign-out") return Http.Json(200, new JObject());
                if (path == "/player-auth/email/password") return Http.Json(200, Http.SignIn("tB", refresh: "svpr1_B", playerId: PlayerB));
                if (path == "/player-auth/refresh")
                {
                    if ((string)Kit.Body(r)["refreshToken"] == "svpr1_A")
                    {
                        await kit.Sv.Auth.SignInWithPasswordAsync("b@example.com", "pw");
                        return Http.Error(401, "refresh_invalid");
                    }
                    return Http.Json(200, Http.SignIn("tX", refresh: "svpr1_X", playerId: PlayerB));
                }
                return r.Headers["Authorization"] == "Bearer t1" ? Http.Error(401, "token_expired") : Http.Json(200, Http.Holdings());
            };
            try { await kit.Sv.Store.GetMineAsync(); } catch (SvApiException) { }
            Console.WriteLine("player=" + kit.Sv.Auth.PlayerId + " stored=" + kit.Recording.RefreshToken() + " refreshes=" + string.Join(",", RefreshesSent(kit)));
            Assert.AreEqual(PlayerB, kit.Sv.Auth.PlayerId);
            Assert.AreEqual("svpr1_B", kit.Recording.RefreshToken(), "B's fresh refresh token was deleted by A's renewal");
        }

        /// <summary>P2b: same player signs in again while the old refresh fails; the old renewal spends the new token.</summary>
        [TestMethod]
        public async Task P2b_SecondRefreshAttempt_NeverSpendsTheNewSignInsRefreshToken()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync("t1", "svpr1_A");
            kit.Transport.Handler = async r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/sign-out") return Http.Json(200, new JObject());
                if (path == "/player-auth/email/password") return Http.Json(200, Http.SignIn("t2", refresh: "svpr1_A2"));
                if (path == "/player-auth/refresh")
                {
                    var sent = (string)Kit.Body(r)["refreshToken"];
                    if (sent == "svpr1_A")
                    {
                        await kit.Sv.Auth.SignInWithPasswordAsync("a@example.com", "pw");
                        return Http.Error(401, "refresh_invalid");
                    }
                    return Http.Json(200, Http.SignIn("t3", refresh: "svpr1_A3"));
                }
                return r.Headers["Authorization"] == "Bearer t1" ? Http.Error(401, "token_expired") : Http.Json(200, Http.Holdings());
            };
            try { await kit.Sv.Store.GetMineAsync(); } catch (SvApiException) { }
            Console.WriteLine("token=" + kit.Sv.Auth.PlayerToken + " stored=" + kit.Recording.RefreshToken() + " refreshes=" + string.Join(",", RefreshesSent(kit)) +
                              " signedOut=" + string.Join(",", SignedOut(kit)));
            CollectionAssert.DoesNotContain(RefreshesSent(kit), "svpr1_A2", "the old epoch's renewal spent the new sign-in's refresh token");
        }

        /// <summary>P1: a restore answer that lands while a game sign-in is saving its refresh token (async store) overwrites it.</summary>
        [TestMethod]
        public async Task P1_RenewalLandingDuringSignInSave_KeepsTheNewPlayersToken()
        {
            var store = new HookStore();
            var first = new Kit(store: store);
            await first.SignInByEmailAsync("t1", "svpr1_A");
            var kit = new Kit(store: store); // next launch
            var gate = new TaskCompletionSource<SvHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            kit.Transport.Handler = r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/sign-out") return Http.Done(Http.Json(200, new JObject()));
                if (path == "/player-auth/refresh") return gate.Task;
                if (path == "/player-auth/email/password") return Http.Done(Http.Json(200, Http.SignIn("tB", refresh: "svpr1_B", playerId: PlayerB)));
                return Http.Done(Http.Error(404, "not_found"));
            };
            var restore = kit.Sv.Auth.RestoreAsync();
            store.OnSet = "refresh_token";
            store.Hook = async () =>
            {
                gate.SetResult(Http.Json(200, Http.SignIn("tA2", refresh: "svpr1_A2")));
                try { await restore; } catch (Exception) { }
            };
            await kit.Sv.Auth.SignInWithPasswordAsync("b@example.com", "pw");
            var raw = await store.GetAsync(kit.Sv.StoragePrefix + "refresh_token");
            Console.WriteLine("player=" + kit.Sv.Auth.PlayerId + " stored=" + raw + " restore=" + restore.Result + " signedOut=" + string.Join(",", SignedOut(kit)));
            Assert.AreEqual(PlayerB, kit.Sv.Auth.PlayerId);
            StringAssert.Contains(raw ?? "", "svpr1_B", "B's refresh token was replaced by the late restore's");
        }
    }
}

namespace SplatterVault.Core.Tests
{
    [TestClass]
    public class AskGameProbes
    {
        private const string SteamPlayer = "bbbbbbbb-0000-4000-8000-000000000002";

        /// <summary>P3: a request in flight across SignOutAsync, answered 401 token_expired, still asks the game to sign in again.</summary>
        [TestMethod]
        public async Task P3_LateTokenExpired_AfterSignOut_NeverCallsReauthenticate()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("tS", provider: "steam", playerId: SteamPlayer)));
            await kit.Sv.Auth.SignInWithSteamAsync("AB");
            var asked = 0;
            var required = 0;
            kit.Sv.Auth.SignInRequired += _ => required++;
            kit.Sv.Auth.Reauthenticate = async (info, ct) =>
            {
                asked++;
                await kit.Sv.Auth.SignInWithSteamAsync("CD"); // what the Steam sample does
                return kit.Sv.Auth.IsSignedIn;
            };
            var gate = new TaskCompletionSource<SvHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            kit.Transport.Handler = r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/steam") return Http.Done(Http.Json(200, Http.SignIn("tS2", provider: "steam", playerId: SteamPlayer)));
                if (path == "/store/me") return r.Headers["Authorization"] == "Bearer tS" ? gate.Task : Http.Done(Http.Json(200, Http.Holdings()));
                return Http.Done(Http.Json(200, new JObject()));
            };
            var call = kit.Sv.Store.GetMineAsync();         // in flight with tS
            await kit.Sv.Auth.SignOutAsync();               // the player signs out
            gate.SetResult(Http.Error(401, "token_expired"));
            try { await call; } catch (SvApiException) { }
            Console.WriteLine("asked=" + asked + " required=" + required + " signedIn=" + kit.Sv.Auth.IsSignedIn + " last=" + await kit.Sv.Auth.GetLastSignInMethodAsync());
            Assert.AreEqual(0, asked, "a late 401 after sign-out ran Reauthenticate");
            Assert.IsFalse(kit.Sv.Auth.IsSignedIn, "the signed-out player was signed back in");
        }
    }
}

namespace SplatterVault.Core.Tests
{
    [TestClass]
    public class StoreProbes
    {
        /// <summary>P4: a purchase answered 429 (long Retry-After) is not outcome-unknown, yet stays pending and is applied by a later resume.</summary>
        [TestMethod]
        public async Task P4_RateLimitedPurchase_IsFlaggedPending()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync("t1", "svpr1_A");
            kit.Transport.Handler = r => Http.Done(Http.Error(429, "rate_limited", "slow down", ("Retry-After", "30")));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword", "GOLD", 500));
            var pending = await kit.Sv.Store.GetPendingAsync();
            Console.WriteLine("status=" + e.Status + " code=" + e.ErrorCode + " unknown=" + e.IsOutcomeUnknown + " network=" + e.IsNetworkError + " pending=" + pending.Count);
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.Txn(false)));
            var resumed = await kit.Sv.Store.ResumePendingAsync();
            Console.WriteLine("resumed=" + resumed.Count + " purchasesSent=" + kit.Transport.To("POST", "/store/me/purchases").Count);
            // Adapted to rule 16: a 429 proves nothing was applied (IsOutcomeUnknown stays false), but the change is kept
            // for a later resume, so the error must say so (IsPending), and the resume applies it exactly once.
            Assert.IsFalse(e.IsOutcomeUnknown);
            Assert.IsTrue(e.IsPending, "kept for a later resume, but the error does not say so");
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual(e.IdempotencyKey, pending[0].IdempotencyKey);
            Assert.AreEqual(1, resumed.Count);
        }
    }
}

namespace SplatterVault.Core.Tests
{
    [TestClass]
    public class DeadlockProbes
    {
        private const string SteamPlayer = "bbbbbbbb-0000-4000-8000-000000000002";

        /// <summary>P5: Reauthenticate that resumes pending changes, while a purchase waits on that reauth.</summary>
        [TestMethod]
        public async Task P5_ReauthenticateThatResumes_WhileAPurchaseWaits_DoesNotDeadlock()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("tS", provider: "steam", playerId: SteamPlayer)));
            await kit.Sv.Auth.SignInWithSteamAsync("AB");
            kit.Sv.Auth.Reauthenticate = async (info, ct) =>
            {
                await kit.Sv.Auth.SignInWithSteamAsync("CD");
                await kit.Sv.Store.ResumePendingAsync(); // "after sign-in: finish changes a crash interrupted"
                return kit.Sv.Auth.IsSignedIn;
            };
            kit.Transport.Handler = r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/steam") return Http.Done(Http.Json(200, Http.SignIn("tS2", provider: "steam", playerId: SteamPlayer)));
                if (path == "/store/me/purchases")
                    return Http.Done(r.Headers["Authorization"] == "Bearer tS" ? Http.Error(401, "token_expired") : Http.Json(200, Http.Txn(false)));
                return Http.Done(Http.Json(200, new JObject()));
            };
            var buy = kit.Sv.Store.PurchaseAsync("sword", "GOLD", 500);
            var done = await Task.WhenAny(buy, Task.Delay(5000));
            Console.WriteLine("completed=" + (done == buy) + " status=" + buy.Status);
            Assert.AreSame(buy, done, "purchase and Reauthenticate wait on each other forever");
        }
    }
}
