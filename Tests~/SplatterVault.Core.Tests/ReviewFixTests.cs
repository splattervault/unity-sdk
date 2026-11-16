using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SplatterVault.Players;
using SplatterVault.Players.Unity;

namespace SplatterVault.Core.Tests
{
    /// <summary>The review round of SDK 4.0 (storage errors, refresh timing, clocks, ownership, restore, namespaces).</summary>
    [TestClass]
    public class StorageErrorTests
    {
        private static Kit FlakyKit(out FlakyStore flaky, SvMemorySecureStore inner = null)
        {
            var f = new FlakyStore(inner ?? new SvMemorySecureStore());
            flaky = f;
            return new Kit(store: f);
        }

        [TestMethod]
        public async Task UnreadableGuestSecret_IsNeverReplacedByANewGuest_AndIsReadAgainNextTime()
        {
            var inner = new SvMemorySecureStore();
            var seed = new Kit(store: inner);
            seed.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_ONLY", provider: "device")));
            await seed.Sv.Auth.SignInAsGuestAsync();

            var kit = FlakyKit(out var flaky, inner);
            flaky.FailReads.Add("device_secret");
            var failures = new List<SvStorageFailure>();
            kit.Sv.Auth.StorageFailed += failures.Add;
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t9", device: "svpd1_NEW", provider: "device")));

            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Auth.SignInAsGuestAsync());
            Assert.AreEqual(SvApiException.StorageUnreadableCode, e.ErrorCode);
            Assert.AreEqual(0, e.Status);
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Auth.CreateGuestAsync());
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Auth.HasGuestOnDeviceAsync());
            Assert.AreEqual(0, kit.Transport.Requests.Count, "no guest was made");
            Assert.IsFalse(flaky.Writes.Contains("device_secret"), "the only copy was not overwritten");
            Assert.IsTrue(failures.All(f => f.Item == "guest_secret" && f.Operation == SvStorageOperation.Read));
            StringAssert.Contains((string)JObject.Parse(await inner.GetAsync(kit.Sv.StoragePrefix + "device_secret"))["secret"], "svpd1_ONLY");

            flaky.FailReads.Clear(); // the keychain unlocks: the failure was not cached
            await kit.Sv.Auth.SignInAsGuestAsync();
            Assert.AreEqual("/player-auth/device", FakeTransport.PathOf(kit.Transport.Requests.Single()));
            Assert.AreEqual("{\"gameId\":123,\"deviceSecret\":\"svpd1_ONLY\"}", kit.Transport.Requests.Single().Body);
        }

        [TestMethod]
        public async Task ReplaceExistingGuest_IsTheExplicitWayOut()
        {
            var kit = FlakyKit(out var flaky);
            flaky.FailReads.Add("device_secret"); // sealed on another device (a restored backup)
            flaky.HealOnWrite = true;
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_NEW", provider: "device")));
            var result = await kit.Sv.Auth.CreateGuestAsync(replaceExistingGuest: true);
            Assert.IsTrue(result.SavedToDevice);
            Assert.AreEqual("/player-auth/device/new", FakeTransport.PathOf(kit.Transport.Requests.Single()));
        }

        [TestMethod]
        public async Task NewGuestSecret_ThatCannotBeSaved_IsReported_AndNotCalledRenewable()
        {
            var kit = FlakyKit(out var flaky);
            flaky.FailWrites.Add("device_secret");
            var failures = new List<SvStorageFailure>();
            kit.Sv.Auth.StorageFailed += failures.Add;
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_X", provider: "device")));
            var result = await kit.Sv.Auth.SignInAsGuestAsync();
            Assert.IsFalse(result.SavedToDevice);
            Assert.IsFalse(result.Renewable);
            Assert.AreEqual(SvStorageOperation.Write, failures.Single(f => f.Item == "guest_secret").Operation);

            flaky.FailWrites.Clear();
            Assert.IsTrue(await kit.Sv.Auth.RetrySavingSecretsAsync());
            Assert.IsTrue(flaky.Writes.Contains("device_secret"));
            Assert.IsTrue(await new Kit(store: flaky).Sv.Auth.HasGuestOnDeviceAsync(), "saved now: a restart finds it");
        }

        [TestMethod]
        public async Task UnsavedRefreshToken_IsReported()
        {
            var kit = FlakyKit(out var flaky);
            flaky.FailWrites.Add("refresh_token");
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", refresh: "svpr1_A")));
            var result = await kit.Sv.Auth.SignInWithPasswordAsync("nova@example.com", "pw-pw-pw-pw");
            Assert.IsFalse(result.SavedToDevice);
            Assert.IsFalse(result.Renewable);
        }

        [TestMethod]
        public async Task UnreadableRefreshToken_IsNeitherSignedOutNorDeleted()
        {
            var email = new Kit();
            await email.SignInByEmailAsync();
            var flaky = new FlakyStore(email.Store);
            flaky.FailReads.Add("refresh_token");
            var relaunch = new Kit(store: flaky);
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => relaunch.Sv.Auth.RestoreAsync());
            Assert.AreEqual(SvApiException.StorageUnreadableCode, e.ErrorCode);
            Assert.AreEqual("svpr1_A", email.Recording.RefreshToken());
            Assert.AreEqual(0, relaunch.Transport.Requests.Count);
        }

        [TestMethod]
        public async Task UnreadablePendingChanges_AreNotOverwrittenByAShorterList()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500));
            var before = kit.Recording.Get("pending_store_changes");
            Assert.IsNotNull(before);

            var flaky = new FlakyStore(kit.Store);
            flaky.FailReads.Add("pending_store_changes");
            var relaunch = new Kit(store: flaky);
            await relaunch.SignInBySteamAsync();
            relaunch.Transport.Handler = r => Http.Done(Http.Json(201, Http.Txn(false)));
            await relaunch.Sv.Store.PurchaseAsync("shield", "GOLD", 100); // still sent
            Assert.AreEqual(before, kit.Recording.Get("pending_store_changes"), "the unreadable list was left alone");
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => relaunch.Sv.Store.GetPendingAsync());
            Assert.AreEqual(SvApiException.StorageUnreadableCode, e.ErrorCode);
        }
    }

    [TestClass]
    public class RefreshTimingTests
    {
        [TestMethod]
        public async Task SlowRefresh_UsesShortSends_SoEveryResendLandsInTheGrace()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            var refreshStarts = new List<double>();
            var refreshTimeouts = new List<TimeSpan>();
            var start = kit.Now;
            kit.Transport.Handler = r =>
            {
                if (FakeTransport.PathOf(r) != "/player-auth/refresh")
                    return Http.Done(r.Headers["Authorization"] == "Bearer t2" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired"));
                refreshStarts.Add((kit.Now - start).TotalSeconds);
                refreshTimeouts.Add(r.Timeout);
                // A slow server: the answer never comes within the send's own timeout.
                kit.Advance(r.Timeout);
                var failed = new TaskCompletionSource<SvHttpResponse>();
                failed.SetException(new SvTransportException(SvTransportFailure.Timeout, "timeout"));
                return failed.Task;
            };
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(SvApiException.TimeoutCode, e.ErrorCode);
            Assert.AreEqual(3, refreshStarts.Count);
            Assert.IsTrue(refreshTimeouts.All(t => t == TimeSpan.FromSeconds(8)), string.Join(",", refreshTimeouts));
            Assert.IsTrue(refreshStarts.All(t => t < 30), "every resend starts inside the API's 30 s grace: " + string.Join(",", refreshStarts));
            Assert.AreEqual(TimeSpan.FromSeconds(30), kit.Transport.To("GET", "/store/me")[0].Timeout, "other requests keep the normal timeout");
            Assert.AreEqual("svpr1_A", kit.Recording.RefreshToken(), "no answer: the token is kept");
        }

        [TestMethod]
        public async Task SlowFirstSend_ThenAnswer_Succeeds()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            var refreshes = 0;
            kit.Transport.Handler = r =>
            {
                if (FakeTransport.PathOf(r) == "/player-auth/refresh")
                {
                    if (++refreshes == 1)
                    {
                        kit.Advance(r.Timeout);
                        var t = new TaskCompletionSource<SvHttpResponse>();
                        t.SetException(new SvTransportException(SvTransportFailure.Timeout, "timeout"));
                        return t.Task;
                    }
                    return Http.Done(Http.Json(200, Http.SignIn("t2", refresh: "svpr1_B")));
                }
                return Http.Done(r.Headers["Authorization"] == "Bearer t2" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired"));
            };
            await kit.Sv.Store.GetMineAsync();
            Assert.AreEqual(2, refreshes);
            Assert.AreEqual("svpr1_B", kit.Recording.RefreshToken());
        }
    }

    [TestClass]
    public class ClockTests
    {
        private static long Unix(DateTimeOffset at) => at.ToUnixTimeSeconds();

        private static async Task<Kit> EmailKitWithServerClock(DateTimeOffset serverNow, DateTimeOffset deviceNow, int lifetimeSeconds = 3600)
        {
            var kit = new Kit();
            kit.Now = deviceNow;
            var refreshCount = 0;
            kit.Transport.Handler = r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/refresh") refreshCount++;
                if (path.StartsWith("/player-auth/")) // sign-in and refresh answers, stamped with the SERVER's clock
                {
                    var token = Http.Jwt(Unix(serverNow), Unix(serverNow) + lifetimeSeconds, tag: "n" + refreshCount);
                    var expiresAt = serverNow.AddSeconds(lifetimeSeconds).ToString("o");
                    return Http.Done(Http.Json(200, Http.SignIn(token, expiresAt, "svpr1_R" + refreshCount)));
                }
                return Http.Done(Http.Json(200, Http.Holdings()));
            };
            await kit.Sv.Auth.SignInWithPasswordAsync("nova@example.com", "pw-pw-pw-pw");
            return kit;
        }

        [TestMethod]
        public async Task DeviceClockAnHourFast_DoesNotRefreshBeforeEveryRequest()
        {
            var server = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
            var kit = await EmailKitWithServerClock(server, server.AddHours(1));
            for (var i = 0; i < 5; i++)
            {
                kit.Now += TimeSpan.FromMinutes(5);
                await kit.Sv.Store.GetMineAsync();
            }
            Assert.AreEqual(0, kit.Transport.To("POST", "/player-auth/refresh").Count);
            Assert.AreEqual(kit.Now.AddMinutes(-25).AddHours(1).AddSeconds(-5), kit.Sv.Auth.TokenExpiresAtUtc, "an hour on THIS clock");
        }

        [TestMethod]
        public async Task DeviceClockAnHourSlow_RenewsOnTime_NotAnHourLate()
        {
            var server = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
            var device = server.AddHours(-1);
            var kit = await EmailKitWithServerClock(server, device);
            kit.Now = device.AddMinutes(30);
            await kit.Sv.Store.GetMineAsync();
            Assert.AreEqual(0, kit.Transport.To("POST", "/player-auth/refresh").Count);
            kit.Now = device.AddMinutes(58).AddSeconds(30); // inside the 120 s lead (minus the 5 s margin)
            await kit.Sv.Store.GetMineAsync();
            Assert.AreEqual(1, kit.Transport.To("POST", "/player-auth/refresh").Count);
        }

        [TestMethod]
        public async Task Lifetime_ComesFromTheToken_ElseOneHour()
        {
            var server = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
            var deviceStart = server.AddMinutes(-7);
            var kit = await EmailKitWithServerClock(server, deviceStart, lifetimeSeconds: 600);
            Assert.AreEqual(deviceStart.AddSeconds(600 - 5), kit.Sv.Auth.TokenExpiresAtUtc);

            var plain = new Kit();
            await plain.SignInBySteamAsync(); // "t1": not a JWT
            Assert.AreEqual(plain.Now.AddHours(1).AddSeconds(-5), plain.Sv.Auth.TokenExpiresAtUtc);
        }

        [TestMethod]
        public async Task ShortLivedToken_IsNotRenewedOnEveryRequest()
        {
            var server = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
            var kit = await EmailKitWithServerClock(server, server, lifetimeSeconds: 60); // shorter than the 120 s lead
            await kit.Sv.Store.GetMineAsync();
            await kit.Sv.Store.GetMineAsync();
            Assert.AreEqual(0, kit.Transport.To("POST", "/player-auth/refresh").Count, "renewed at half its life, not at once");
            kit.Now += TimeSpan.FromSeconds(30);
            await kit.Sv.Store.GetMineAsync();
            Assert.AreEqual(1, kit.Transport.To("POST", "/player-auth/refresh").Count);
        }

        [TestMethod]
        public async Task StudioToken_ExpiryIsCorrectedWithTheAnswersDate()
        {
            var kit = new Kit();
            var server = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
            kit.Now = server.AddHours(2); // device clock two hours fast
            var token = Http.Jwt(Unix(server), Unix(server) + 3600, amr: "studio");
            kit.Transport.Handler = r => Http.Done(Http.Json(200, JObject.Parse("{\"player\":{\"id\":\"p\"},\"game\":{\"gameKey\":\"k\"},\"identities\":[]}"),
                ("Date", server.ToString("r"))));
            await kit.Sv.Auth.SignInWithStudioTokenAsync(token);
            Assert.AreEqual(kit.Now.AddHours(1), kit.Sv.Auth.TokenExpiresAtUtc);
        }
    }

    [TestClass]
    public class GuestOwnershipTests
    {
        private const string Guest = "aaaaaaaa-0000-4000-8000-000000000001";
        private const string SteamPlayer = "bbbbbbbb-0000-4000-8000-000000000002";

        private static async Task<Kit> GuestThenSteamPlayer()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_GUEST", provider: "device", playerId: Guest)));
            await kit.Sv.Auth.SignInAsGuestAsync();
            // Another player, who also has a device identity (on some other phone).
            var steam = Http.SignIn("t2", provider: "steam", playerId: SteamPlayer);
            ((JArray)steam["identities"]).Add(new JObject { ["provider"] = "device", ["linkedAt"] = "2026-11-02T00:00:00.000Z" });
            kit.Transport.Handler = r => Http.Done(Http.Json(200, steam));
            await kit.Sv.Auth.SignInWithSteamAsync("AB");
            return kit;
        }

        [TestMethod]
        public async Task ErasingAnotherPlayer_KeepsThisDevicesGuest()
        {
            var kit = await GuestThenSteamPlayer();
            kit.Transport.Handler = r => Http.Done(new SvHttpResponse(204, ""));
            await kit.Sv.Auth.EraseMeAsync();
            Assert.AreEqual("svpd1_GUEST", kit.Recording.GuestSecret());
        }

        [TestMethod]
        public async Task PlayerDeletedForAnotherPlayer_KeepsThisDevicesGuest()
        {
            var kit = await GuestThenSteamPlayer();
            kit.Transport.Handler = r => Http.Done(Http.Error(401, "player_deleted"));
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            await Task.Delay(20);
            Assert.AreEqual("svpd1_GUEST", kit.Recording.GuestSecret());
        }

        [TestMethod]
        public async Task UnlinkingAnotherPlayersDevice_KeepsThisDevicesGuest()
        {
            var kit = await GuestThenSteamPlayer();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, JObject.Parse("{\"player\":{\"id\":\"" + SteamPlayer + "\"},\"game\":{\"gameKey\":\"k\"},\"identities\":[]}")));
            await kit.Sv.Auth.UnlinkAsync(SvProviders.Device);
            Assert.AreEqual("svpd1_GUEST", kit.Recording.GuestSecret());
        }

        [TestMethod]
        public async Task PlayerDeletedForTheGuestItself_ForgetsItsSecret()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_GUEST", provider: "device", playerId: Guest)));
            await kit.Sv.Auth.SignInAsGuestAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(401, "player_deleted"));
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            await Task.Delay(20);
            Assert.IsNull(kit.Recording.GuestSecret());
        }

        [TestMethod]
        public async Task Restore_NeverUsesAnotherPlayersGuestSecret()
        {
            var store = new SvMemorySecureStore();
            var prefix = new Kit(store: store).Sv.StoragePrefix;
            await store.SetAsync(prefix + "last_sign_in", "{\"method\":\"device\",\"player\":\"" + Guest + "\"}");
            await store.SetAsync(prefix + "device_secret", "{\"v\":1,\"secret\":\"svpd1_OTHER\",\"player\":\"cccccccc-0000-4000-8000-000000000003\"}");
            var kit = new Kit(store: store);
            Assert.IsFalse(await kit.Sv.Auth.RestoreAsync());
            Assert.AreEqual(0, kit.Transport.Requests.Count);
        }
    }

    [TestClass]
    public class ReviewFixMiscTests
    {
        [TestMethod]
        public async Task RefreshInvalid_ThenANewerStoredToken_IsTriedOnce()
        {
            // Renewal racing an account change: the password changed (on this device, through another call) while the
            // old refresh token was on its way, so the API refuses the old one; the newer one, saved meanwhile, works.
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            kit.Transport.Handler = r =>
            {
                if (FakeTransport.PathOf(r) == "/player-auth/refresh")
                {
                    var sent = (string)Kit.Body(r)["refreshToken"];
                    if (sent == "svpr1_A")
                    {
                        kit.Store.SetAsync(kit.Sv.StoragePrefix + "refresh_token",
                            "{\"v\":1,\"token\":\"svpr1_NEWER\",\"player\":\"0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b\"}").Wait();
                        return Http.Done(Http.Error(401, "refresh_invalid"));
                    }
                    return Http.Done(Http.Json(200, Http.SignIn("t2", refresh: "svpr1_C")));
                }
                return Http.Done(r.Headers["Authorization"] == "Bearer t2" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired"));
            };
            await kit.Sv.Store.GetMineAsync();
            CollectionAssert.AreEqual(new[] { "svpr1_A", "svpr1_NEWER" },
                kit.Transport.To("POST", "/player-auth/refresh").Select(r => (string)Kit.Body(r)["refreshToken"]).ToList());
            Assert.AreEqual("svpr1_C", kit.Recording.RefreshToken());
        }

        [TestMethod]
        public async Task RefreshInvalid_KeepsANewerStoredToken()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            kit.Transport.Handler = r =>
            {
                if (FakeTransport.PathOf(r) == "/player-auth/refresh")
                {
                    if ((string)Kit.Body(r)["refreshToken"] == "svpr1_A")
                        kit.Store.SetAsync(kit.Sv.StoragePrefix + "refresh_token",
                            "{\"v\":1,\"token\":\"svpr1_NEWER\",\"player\":\"0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b\"}").Wait();
                    return Http.Done(Http.Error(503, null, "busy")); // the newer one gets no answer: it must survive
                }
                return Http.Done(Http.Error(401, "token_expired", "Token expired"));
            };
            // First refresh_invalid for A (written above), then the newer token's sends fail transiently.
            var calls = 0;
            var inner = kit.Transport.Handler;
            kit.Transport.Handler = r => FakeTransport.PathOf(r) == "/player-auth/refresh" && ++calls == 1
                ? inner(r).ContinueWith(_ => Http.Error(401, "refresh_invalid"))
                : inner(r);
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual("svpr1_NEWER", kit.Recording.RefreshToken());
        }

        [TestMethod]
        public async Task Erase_DropsThatPlayersPendingStoreChanges()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500));
            Assert.AreEqual(1, (await kit.Sv.Store.GetPendingAsync()).Count);
            kit.Transport.Handler = r => Http.Done(new SvHttpResponse(204, ""));
            await kit.Sv.Auth.EraseMeAsync();
            Assert.IsNull(kit.Recording.Get("pending_store_changes"));
        }

        [TestMethod]
        public async Task PlayerDeleted_DropsThatPlayersPendingStoreChanges()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500));
            kit.Transport.Handler = r => Http.Done(Http.Error(401, "player_deleted"));
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            await Task.Delay(20);
            Assert.IsNull(kit.Recording.Get("pending_store_changes"));
        }

        [TestMethod]
        public void StorageNamespace_IgnoresSpelling_AndIsFixed()
        {
            string Prefix(string url) => new SvPlayers(new SvPlayersOptions { GameId = 123, BaseUrl = url, Transport = new FakeTransport() }).StoragePrefix;
            var a = Prefix("https://splattervault.com/rest");
            Assert.AreEqual(a, Prefix("https://SplatterVault.com/rest/"));
            Assert.AreEqual(a, Prefix("HTTPS://splattervault.com:443/rest"));
            Assert.AreEqual(a, Prefix("https://splattervault.com/api/v2"));
            Assert.AreNotEqual(a, Prefix("https://other.splattervault.com/rest"));
            Assert.AreNotEqual(a, Prefix("https://splattervault.com:8443/rest"));
            Assert.AreEqual("https://splattervault.com|id:123", SvPlayers.DefaultStorageNamespace("https://splattervault.com/rest", 123, null));
            // Golden value: changing how the default is made would orphan every stored guest.
            Assert.AreEqual(SvPlayers.StoragePrefixFor("https://splattervault.com|id:123"), a);
            Assert.AreEqual("sv4.", a.Substring(0, 4));
            Assert.AreEqual(4 + 16 + 1, a.Length);
        }

        [TestMethod]
        public async Task StudioToken_RefusedOne_LeavesTheCurrentSignInAlone()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(401, "token_invalid"));
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Auth.SignInWithStudioTokenAsync("bad.token.here"));
            Assert.AreEqual("t1", kit.Sv.Auth.PlayerToken);
            Assert.AreEqual("svpr1_A", kit.Recording.RefreshToken());
            Assert.AreEqual(0, kit.Transport.To("POST", "/player-auth/sign-out").Count);
            Assert.AreEqual("Bearer bad.token.here", kit.Transport.Requests.Last().Headers["Authorization"]);
        }

        [TestMethod]
        public void UnreadableAnswer_NeverQuotesAValue()
        {
            var e = Assert.ThrowsException<SvApiException>(() =>
                SvPipeline.Read<SignInAnswer>(new SvHttpResponse(200, "{\"token\":\"t\",\"player\":\"svpr1_SECRET_VALUE\"}")));
            Assert.AreEqual(SvApiException.InvalidResponseCode, e.ErrorCode);
            Assert.IsFalse(e.Message.Contains("svpr1_SECRET_VALUE"), e.Message);
            Assert.IsNull(e.InnerException);
        }

        [TestMethod]
        public async Task Redirect_IsAnAnswer_NeverFollowedOrRepeated()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(new SvHttpResponse(302, "", new Dictionary<string, string> { ["Location"] = "https://evil.example/" }));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(302, e.Status);
            Assert.IsFalse(e.IsTransient);
            Assert.AreEqual(1, kit.Transport.To("GET", "/store/me").Count);
        }

        [TestMethod]
        public async Task SdkMadeTokenExpired_HasStatusZero_AndSendsNothing()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(401, "token_expired", "Token expired"));
            var first = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(401, first.Status, "the API's own answer");
            var sent = kit.Transport.Requests.Count;
            var again = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual("token_expired", again.ErrorCode);
            Assert.AreEqual(0, again.Status, "made by the SDK: nothing was sent");
            Assert.AreEqual(sent, kit.Transport.Requests.Count);
        }

        [TestMethod]
        public async Task SignInRequired_SaysRefreshInvalid_WhenAnEarlyRefreshWasRefused()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            kit.Now += TimeSpan.FromMinutes(59); // inside the renew lead: the SDK refreshes before the request
            var required = new List<SvSignInRequired>();
            kit.Sv.Auth.SignInRequired += required.Add;
            kit.Transport.Handler = r => Http.Done(FakeTransport.PathOf(r) == "/player-auth/refresh"
                ? Http.Error(401, "refresh_invalid")
                : Http.Error(401, "token_expired", "Token expired"));
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(SvSignInRequiredReason.RefreshInvalid, required.Single().Reason);
            Assert.AreEqual(1, kit.Transport.To("POST", "/player-auth/refresh").Count, "one refresh, not one per path");
        }
    }

#pragma warning disable CA1416 // Unix file modes: the test is skipped on Windows
    [TestClass]
    public class FilePermissionTests
    {
        [TestMethod]
        public async Task FallbackFiles_AreOwnerOnly()
        {
            if (Path.DirectorySeparatorChar != '/') Assert.Inconclusive("Unix only");
            var dir = Path.Combine(Path.GetTempPath(), "sv-perm-" + Guid.NewGuid().ToString("N"));
            try
            {
                Action<string, bool> restrict = (path, isDirectory) => Assert.IsTrue(SvUnixPermissions.Restrict(path, isDirectory), path);
                var salt = SvAesBlobProtector.LoadOrCreateSalt(Path.Combine(dir, "store.salt"), restrict);
                var store = new SvProtectedFileStore(dir, new SvAesBlobProtector("m", salt), null, restrict);
                await store.SetAsync("k", "v");
                await store.SetAsync("k", "v2"); // the replace path too
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
                foreach (var file in Directory.GetFiles(dir))
                    Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file), file);
                Assert.AreEqual("v2", await store.GetAsync("k"));
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
                    Directory.Delete(dir);
                }
            }
        }
    }
#pragma warning restore CA1416
}
