using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SplatterVault.Players;

namespace SplatterVault.Core.Tests
{
    /// <summary>Review round 3: refresh-token ownership, concurrent guests, unreadable pending lists, odd claims, clocks.</summary>
    [TestClass]
    public class RefreshOwnershipTests
    {
        private const string EmailPlayer = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b";
        private const string SteamPlayer = "bbbbbbbb-0000-4000-8000-000000000002";

        [TestMethod]
        public async Task LeftoverRefreshToken_OfAnotherPlayer_NeverSwitchesTheSession()
        {
            var flaky = new FlakyStore(new SvMemorySecureStore());
            var kit = new Kit(store: flaky);
            await kit.SignInByEmailAsync(); // player E, refresh token svpr1_A
            Assert.AreEqual(EmailPlayer, kit.Recording.RefreshOwner() ?? (string)JObject.Parse(await flaky.RawAsync(kit.Sv.StoragePrefix + "refresh_token"))["player"]);

            flaky.FailDeletes.Add("refresh_token"); // E's token cannot be deleted when S signs in
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t2", provider: "steam", playerId: SteamPlayer)));
            await kit.Sv.Auth.SignInWithSteamAsync("AB");
            StringAssert.Contains(await flaky.RawAsync(kit.Sv.StoragePrefix + "refresh_token"), "svpr1_A", "the leftover is still stored");

            var required = new List<SvSignInRequired>();
            kit.Sv.Auth.SignInRequired += required.Add;
            kit.Transport.Handler = r =>
            {
                if (FakeTransport.PathOf(r) == "/player-auth/refresh") return Http.Done(Http.Json(200, Http.SignIn("t9", refresh: "svpr1_Z")));
                return Http.Done(r.Headers["Authorization"] == "Bearer t2" && kit.Now < DateTimeOffset.Parse("2026-11-14T13:00:00Z")
                    ? Http.Json(200, Http.Holdings())
                    : Http.Error(401, "token_expired", "Token expired"));
            };
            kit.Advance(TimeSpan.FromMinutes(58.5)); // inside the renew lead: an early renewal would happen now
            await kit.Sv.Store.GetMineAsync();
            Assert.AreEqual(SteamPlayer, kit.Sv.Auth.PlayerId);
            Assert.AreEqual(0, kit.Transport.To("POST", "/player-auth/refresh").Count, "E's token was not used");

            kit.Advance(TimeSpan.FromMinutes(5)); // the Steam token runs out: a fresh Steam proof is needed, never E's token
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(0, kit.Transport.To("POST", "/player-auth/refresh").Count);
            Assert.AreEqual(SvProviders.Steam, required.Single().Method);
            Assert.IsTrue(flaky.DeleteAttempts.Count(d => d == "refresh_token") >= 2, "the leftover is deleted again");
        }

        [TestMethod]
        public async Task Restore_UsesOnlyTheLastEmailPlayersToken()
        {
            var store = new SvMemorySecureStore();
            var prefix = new Kit(store: store).Sv.StoragePrefix;
            await store.SetAsync(prefix + "last_sign_in", "{\"method\":\"email\",\"player\":\"" + EmailPlayer + "\"}");
            await store.SetAsync(prefix + "refresh_token", "{\"v\":1,\"token\":\"svpr1_X\",\"player\":\"" + SteamPlayer + "\"}");
            var kit = new Kit(store: store);
            Assert.IsFalse(await kit.Sv.Auth.RestoreAsync());
            Assert.AreEqual(0, kit.Transport.Requests.Count);
            Assert.IsNull(await store.GetAsync(prefix + "refresh_token"), "the mismatched token was deleted");

            await store.SetAsync(prefix + "refresh_token", "svpr1_BARE"); // no player at all: never used
            Assert.IsFalse(await new Kit(store: store).Sv.Auth.RestoreAsync());
        }

        [TestMethod]
        public async Task RefreshTokens_AreStoredWithTheirPlayer()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            Assert.AreEqual(EmailPlayer, kit.Recording.RefreshOwner());
            kit.Transport.Handler = r => Http.Done(Http.Json(200, JObject.Parse("{\"refreshToken\":\"svpr1_P\",\"refreshExpiresAt\":\"x\"}")));
            await kit.Sv.Auth.ChangePasswordAsync("old password", "new password");
            Assert.AreEqual("svpr1_P", kit.Recording.RefreshToken());
            Assert.AreEqual(EmailPlayer, kit.Recording.RefreshOwner());
        }
    }

    [TestClass]
    public class ConcurrentGuestTests
    {
        [TestMethod]
        public async Task TwoGuestSignInsAtOnce_MakeOneGuest()
        {
            var kit = new Kit();
            var gate = new TaskCompletionSource<SvHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            kit.Transport.Handler = r => FakeTransport.PathOf(r) == "/player-auth/device/new"
                ? gate.Task
                : Http.Done(Http.Json(200, Http.SignIn("t2", provider: "device")));
            var first = kit.Sv.Auth.SignInAsGuestAsync("A");
            var second = kit.Sv.Auth.SignInAsGuestAsync("B");
            await Task.Delay(50);
            gate.SetResult(Http.Json(200, Http.SignIn("t1", device: "svpd1_ONE", provider: "device")));
            var results = await Task.WhenAll(first, second);
            Assert.AreEqual(1, kit.Transport.Exactly("POST", "/player-auth/device/new").Count);
            Assert.AreEqual(0, kit.Transport.Exactly("POST", "/player-auth/device").Count, "the second caller shared the first's answer");
            Assert.AreEqual(results[0].Player.Id, results[1].Player.Id);
            Assert.AreEqual("svpd1_ONE", kit.Recording.GuestSecret());
        }

        [TestMethod]
        public async Task CreateGuest_WaitsForAGuestSignInInFlight()
        {
            var kit = new Kit();
            var gate = new TaskCompletionSource<SvHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            kit.Transport.Handler = r => gate.Task;
            var signIn = kit.Sv.Auth.SignInAsGuestAsync();
            var create = kit.Sv.Auth.CreateGuestAsync();
            await Task.Delay(50);
            gate.SetResult(Http.Json(200, Http.SignIn("t1", device: "svpd1_ONE", provider: "device")));
            await signIn;
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => create, "it saw the guest the first call made");
            Assert.AreEqual(1, kit.Transport.Exactly("POST", "/player-auth/device/new").Count);
        }

        [TestMethod]
        public async Task GuestSignIn_CanBeAwaitedAgainAfterwards()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_ONE", provider: "device")));
            await kit.Sv.Auth.SignInAsGuestAsync();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t2", provider: "device")));
            await kit.Sv.Auth.SignInAsGuestAsync(); // a later call signs in with the stored secret
            Assert.AreEqual(1, kit.Transport.Exactly("POST", "/player-auth/device").Count);
            Assert.AreEqual("t2", kit.Sv.Auth.PlayerToken);
        }
    }

    [TestClass]
    public class UnreadablePendingListTests
    {
        private static async Task<(Kit kit, FlakyStore flaky)> WithUnreadableList()
        {
            var seed = new Kit();
            await seed.SignInBySteamAsync();
            seed.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => seed.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500));
            var flaky = new FlakyStore(seed.Store);
            flaky.FailReads.Add("pending_store_changes"); // e.g. restored from a backup without its Keystore key
            var kit = new Kit(store: flaky);
            await kit.SignInBySteamAsync();
            return (kit, flaky);
        }

        [TestMethod]
        public async Task Discard_DeletesAnUnreadableList()
        {
            var (kit, flaky) = await WithUnreadableList();
            await kit.Sv.Store.DiscardPendingAsync("whatever");
            Assert.IsNull(await flaky.RawAsync(kit.Sv.StoragePrefix + "pending_store_changes"));
        }

        [TestMethod]
        public async Task DiscardAll_NeverReads()
        {
            var (kit, flaky) = await WithUnreadableList();
            await kit.Sv.Store.DiscardAllPendingAsync();
            Assert.IsNull(await flaky.RawAsync(kit.Sv.StoragePrefix + "pending_store_changes"));
        }

        [TestMethod]
        public async Task Erase_DeletesAnUnreadableList()
        {
            var (kit, flaky) = await WithUnreadableList();
            kit.Transport.Handler = r => Http.Done(new SvHttpResponse(204, ""));
            await kit.Sv.Auth.EraseMeAsync();
            Assert.IsNull(await flaky.RawAsync(kit.Sv.StoragePrefix + "pending_store_changes"));
        }

        [TestMethod]
        public async Task APurchase_RaisesOneStorageErrorNotTwo()
        {
            var (kit, flaky) = await WithUnreadableList();
            var failures = new List<SvStorageFailure>();
            kit.Sv.Auth.StorageFailed += failures.Add;
            kit.Transport.Handler = r => Http.Done(Http.Json(201, Http.Txn(false)));
            await kit.Sv.Store.PurchaseAsync("shield", "GOLD", 100);
            Assert.AreEqual(1, failures.Count(f => f.Item == "pending_store_changes"));
        }
    }

    [TestClass]
    public class OddClaimsTests
    {
        private static Kit KitAnswering(JObject claims)
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(FakeTransport.PathOf(r).StartsWith("/player-auth/")
                ? Http.Json(200, Http.SignIn(Http.JwtWith(claims), device: "svpd1_G", provider: "device"))
                : Http.Json(200, Http.Holdings()));
            return kit;
        }

        private static long NowUnix => DateTimeOffset.Parse("2026-11-14T12:00:00Z").ToUnixTimeSeconds();

        [TestMethod]
        public async Task OutOfRangeClaims_FallBackToOneHour_AndNeverThrowAfterTheSecretIsSaved()
        {
            var kit = KitAnswering(new JObject { ["iat"] = 1e300, ["exp"] = 1e300 + 3600 });
            var result = await kit.Sv.Auth.SignInAsGuestAsync();
            Assert.IsTrue(result.SavedToDevice);
            Assert.AreEqual(kit.Now.AddHours(1).AddSeconds(-5), kit.Sv.Auth.TokenExpiresAtUtc);

            var negative = KitAnswering(new JObject { ["iat"] = -5, ["exp"] = 99999999999999 });
            await negative.Sv.Auth.SignInAsGuestAsync();
            Assert.AreEqual(negative.Now.AddHours(1).AddSeconds(-5), negative.Sv.Auth.TokenExpiresAtUtc);

            var huge = KitAnswering(new JObject { ["iat"] = JToken.Parse("123456789012345678901234567890"), ["exp"] = 1 });
            await huge.Sv.Auth.SignInAsGuestAsync();
            Assert.AreEqual(huge.Now.AddHours(1).AddSeconds(-5), huge.Sv.Auth.TokenExpiresAtUtc);
        }

        [TestMethod]
        public async Task TinyLifetime_IsRaisedToAMinute_SoRequestsDoNotEachRefresh()
        {
            var kit = KitAnswering(new JObject { ["iat"] = NowUnix, ["exp"] = NowUnix + 5 });
            await kit.Sv.Auth.SignInAsGuestAsync();
            Assert.AreEqual(kit.Now.AddSeconds(60 - 5), kit.Sv.Auth.TokenExpiresAtUtc);
            await kit.Sv.Store.GetMineAsync();
            await kit.Sv.Store.GetMineAsync();
            Assert.AreEqual(0, kit.Transport.Exactly("POST", "/player-auth/device").Count, "no renewal before every request");
        }

        [TestMethod]
        public async Task BackwardsOrHugeLifetimes_AreKeptSane()
        {
            var backwards = KitAnswering(new JObject { ["iat"] = NowUnix, ["exp"] = NowUnix - 100 });
            await backwards.Sv.Auth.SignInAsGuestAsync();
            Assert.AreEqual(backwards.Now.AddHours(1).AddSeconds(-5), backwards.Sv.Auth.TokenExpiresAtUtc);

            var week = KitAnswering(new JObject { ["iat"] = NowUnix, ["exp"] = NowUnix + 7 * 86400 });
            await week.Sv.Auth.SignInAsGuestAsync();
            Assert.AreEqual(week.Now.AddHours(24).AddSeconds(-5), week.Sv.Auth.TokenExpiresAtUtc);
        }

        [TestMethod]
        public async Task StudioToken_WithoutDate_KeepsTheApisExpiry()
        {
            var kit = new Kit();
            var exp = NowUnix + 1800;
            kit.Transport.Handler = r => Http.Done(Http.Json(200, JObject.Parse("{\"player\":{\"id\":\"p\"},\"game\":{\"gameKey\":\"k\"},\"identities\":[]}")));
            await kit.Sv.Auth.SignInWithStudioTokenAsync(Http.Jwt(NowUnix, exp, amr: "studio"));
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(exp), kit.Sv.Auth.TokenExpiresAtUtc);
        }

        [TestMethod]
        public async Task StudioToken_WithOutOfRangeExp_DoesNotThrow()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, JObject.Parse("{\"player\":{\"id\":\"p\"},\"game\":{\"gameKey\":\"k\"},\"identities\":[]}")));
            await kit.Sv.Auth.SignInWithStudioTokenAsync(Http.JwtWith(new JObject { ["exp"] = 1e300, ["amr"] = new JArray("studio") }));
            Assert.IsTrue(kit.Sv.Auth.IsSignedIn);
            Assert.IsNull(kit.Sv.Auth.TokenExpiresAtUtc);
        }
    }

    [TestClass]
    public class RefreshWindowTests
    {
        private static Kit SlowRefreshKit(Action<Kit, SvHttpRequest> onSend, Action<SvPlayersOptions> tweak = null)
        {
            Kit kit = null;
            kit = new Kit(o =>
            {
                o.RetryPolicy = new SvRetryPolicy { Jitter = 0, MaxAttempts = 10 }; // so only the window can stop it
                tweak?.Invoke(o);
            });
            kit.SignInByEmailAsync().GetAwaiter().GetResult();
            kit.Transport.Handler = r =>
            {
                if (FakeTransport.PathOf(r) != "/player-auth/refresh") return Http.Done(Http.Error(401, "token_expired", "Token expired"));
                onSend(kit, r);
                var t = new TaskCompletionSource<SvHttpResponse>();
                t.SetException(new SvTransportException(SvTransportFailure.Timeout, "timeout"));
                return t.Task;
            };
            return kit;
        }

        [TestMethod]
        public async Task TheResendWindow_StopsResends_NotTheAttemptCount()
        {
            var starts = new List<double>();
            TimeSpan origin = TimeSpan.Zero;
            var kit = SlowRefreshKit((k, r) =>
            {
                if (starts.Count == 0) origin = k.Mono;
                starts.Add((k.Mono - origin).TotalSeconds);
                k.Advance(r.Timeout);
            });
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            // Sends start at 0, 8.5 and 17.5 s; a 4th would start at 27.5 s, past the 25 s window.
            CollectionAssert.AreEqual(new[] { 0d, 8.5, 17.5 }, starts);
        }

        [TestMethod]
        public async Task ATimeSyncBackwards_CannotStretchTheWindow()
        {
            var starts = 0;
            var kit = SlowRefreshKit((k, r) =>
            {
                starts++;
                k.Mono += r.Timeout;
                k.Now -= TimeSpan.FromHours(1); // the device's clock is set back while waiting
            });
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(3, starts);
        }

        [TestMethod]
        public async Task ATimeSyncForwards_CannotCutTheWindow()
        {
            var starts = 0;
            var kit = SlowRefreshKit((k, r) =>
            {
                starts++;
                k.Mono += r.Timeout;
                k.Now += TimeSpan.FromDays(1);
            });
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(3, starts);
        }

        [TestMethod]
        public async Task RefreshTimeout_TenSecondsIsUsed_MoreIsRefusedAtConfiguration()
        {
            var timeouts = new List<TimeSpan>();
            var kit = SlowRefreshKit((k, r) =>
            {
                timeouts.Add(r.Timeout);
                k.Advance(r.Timeout);
            }, o => o.RefreshTimeout = TimeSpan.FromSeconds(10));
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.IsTrue(timeouts.Count > 0 && timeouts.All(t => t == TimeSpan.FromSeconds(10)), string.Join(",", timeouts));
            // Rule 6: an out-of-range value is refused when the client is made, never clamped quietly.
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new Kit(o => o.RefreshTimeout = TimeSpan.FromSeconds(60)));
        }
    }

    [TestClass]
    public class LastSignInReadTests
    {
        [TestMethod]
        public async Task UnreadableLastSignIn_IsAStorageError_NotSignedOut()
        {
            var email = new Kit();
            await email.SignInByEmailAsync();
            var flaky = new FlakyStore(email.Store);
            flaky.FailReads.Add("last_sign_in");
            var relaunch = new Kit(store: flaky);
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => relaunch.Sv.Auth.RestoreAsync());
            Assert.AreEqual(SvApiException.StorageUnreadableCode, e.ErrorCode);
            var e2 = await Assert.ThrowsExceptionAsync<SvApiException>(() => relaunch.Sv.Auth.GetLastSignInMethodAsync());
            Assert.AreEqual(SvApiException.StorageUnreadableCode, e2.ErrorCode);
            Assert.AreEqual("svpr1_A", email.Recording.RefreshToken(), "nothing deleted");
            Assert.AreEqual(0, relaunch.Transport.Requests.Count);
        }
    }
}
