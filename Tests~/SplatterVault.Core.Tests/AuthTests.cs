using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SplatterVault.Players;

namespace SplatterVault.Core.Tests
{
    [TestClass]
    public class RefreshTests
    {
        [TestMethod]
        public async Task ConcurrentExpiredRequests_ShareOneRefresh()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            var gate = new TaskCompletionSource<SvHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            kit.Transport.Handler = r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/refresh") return gate.Task;
                return Http.Done(r.Headers["Authorization"] == "Bearer t2" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired"));
            };
            var calls = new[] { kit.Sv.Store.GetMineAsync(), kit.Sv.Store.GetMineAsync(), kit.Sv.Store.GetMineAsync() };
            await Task.Delay(50);
            Assert.AreEqual(1, kit.Transport.To("POST", "/player-auth/refresh").Count, "one refresh in flight");
            gate.SetResult(Http.Json(200, Http.SignIn("t2", refresh: "svpr1_B")));
            var results = await Task.WhenAll(calls);
            Assert.AreEqual(3, results.Length);
            Assert.AreEqual(1, kit.Transport.To("POST", "/player-auth/refresh").Count);
            Assert.AreEqual("svpr1_A", (string)Kit.Body(kit.Transport.To("POST", "/player-auth/refresh")[0])["refreshToken"]);
            Assert.AreEqual("svpr1_B", kit.Recording.RefreshToken());
            Assert.AreEqual("t2", kit.Sv.Auth.PlayerToken);
        }

        [TestMethod]
        public async Task NewRefreshToken_IsSavedBeforeTheNewPlayerTokenIsUsed()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            kit.Transport.Handler = r =>
            {
                if (FakeTransport.PathOf(r) == "/player-auth/refresh") return Http.Done(Http.Json(200, Http.SignIn("t2", refresh: "svpr1_B")));
                return Http.Done(r.Headers["Authorization"] == "Bearer t2" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired"));
            };
            await kit.Sv.Store.GetMineAsync();
            var events = kit.Transport.Events;
            var saved = events.FindIndex(e => e.StartsWith("store set refresh_token=") && e.Contains("svpr1_B"));
            var used = events.FindIndex(e => e.EndsWith("Bearer t2"));
            Assert.IsTrue(saved >= 0 && used >= 0, string.Join("\n", events));
            Assert.IsTrue(saved < used, string.Join("\n", events));
        }

        [TestMethod]
        public async Task RefreshInvalid_ForgetsTheToken_TellsTheGameOnce_AndStopsSending()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            var required = new List<SvSignInRequired>();
            kit.Sv.Auth.SignInRequired += required.Add;
            kit.Transport.Handler = r => Http.Done(FakeTransport.PathOf(r) == "/player-auth/refresh"
                ? Http.Error(401, "refresh_invalid")
                : Http.Error(401, "token_expired", "Token expired"));

            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual("token_expired", e.ErrorCode);
            Assert.IsNull(kit.Recording.RefreshToken());
            Assert.AreEqual(1, required.Count);
            Assert.AreEqual(SvSignInRequiredReason.RefreshInvalid, required[0].Reason);
            Assert.AreEqual("refresh_invalid", required[0].ErrorCode);

            var sent = kit.Transport.Requests.Count;
            var again = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual("token_expired", again.ErrorCode);
            Assert.AreEqual(sent, kit.Transport.Requests.Count, "a refused token is not sent again");
            Assert.AreEqual(1, required.Count);
        }

        [TestMethod]
        public async Task LostRefreshAnswer_ResendsTheSameRefreshToken()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            var refreshes = 0;
            kit.Transport.Handler = r =>
            {
                if (FakeTransport.PathOf(r) == "/player-auth/refresh")
                    return ++refreshes == 1 ? Http.NetworkFailure() : Http.Done(Http.Json(200, Http.SignIn("t2", refresh: "svpr1_B")));
                return Http.Done(r.Headers["Authorization"] == "Bearer t2" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired"));
            };
            await kit.Sv.Store.GetMineAsync();
            var sent = kit.Transport.To("POST", "/player-auth/refresh").Select(r => (string)Kit.Body(r)["refreshToken"]).ToList();
            CollectionAssert.AreEqual(new[] { "svpr1_A", "svpr1_A" }, sent);
            Assert.AreEqual("svpr1_B", kit.Recording.RefreshToken());
        }

        [TestMethod]
        public async Task TokenAboutToRunOut_IsRenewedBeforeTheRequest()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync(); // a 1-hour token (no iat/exp in it), renewed 120 s ahead on this device's clock
            kit.Now += TimeSpan.FromMinutes(59);
            kit.Transport.Handler = r => Http.Done(FakeTransport.PathOf(r) == "/player-auth/refresh"
                ? Http.Json(200, Http.SignIn("t2", refresh: "svpr1_B"))
                : Http.Json(200, Http.Holdings()));
            await kit.Sv.Store.GetMineAsync();
            var gets = kit.Transport.To("GET", "/store/me");
            Assert.AreEqual(1, gets.Count);
            Assert.AreEqual("Bearer t2", gets[0].Headers["Authorization"]);
        }

        [TestMethod]
        public async Task SteamSession_AsksTheGameForANewProof_AndRetriesOnce()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            var asked = 0;
            kit.Sv.Auth.Reauthenticate = async (info, ct) =>
            {
                asked++;
                Assert.AreEqual(SvSignInRequiredReason.TokenExpired, info.Reason);
                Assert.AreEqual(SvProviders.Steam, info.Method);
                await kit.Sv.Auth.SignInWithSteamAsync("0011AABB");
                return true;
            };
            kit.Transport.Handler = r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/steam") return Http.Done(Http.Json(200, Http.SignIn("t2", provider: "steam")));
                return Http.Done(r.Headers["Authorization"] == "Bearer t2" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired"));
            };
            await kit.Sv.Store.GetMineAsync();
            Assert.AreEqual(1, asked);
            Assert.AreEqual(0, kit.Transport.To("POST", "/player-auth/refresh").Count, "a Steam sign-in has no refresh token");
            CollectionAssert.AreEqual(new[] { "Bearer t1", "Bearer t2" },
                kit.Transport.To("GET", "/store/me").Select(r => r.Headers["Authorization"]).ToList());
        }

        [TestMethod]
        public async Task Reauthenticate_IsNotLooped_WhenTheNewTokenIsRefusedToo()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            var asked = 0;
            kit.Sv.Auth.Reauthenticate = async (info, ct) =>
            {
                asked++;
                await kit.Sv.Auth.SignInWithSteamAsync("0011AABB");
                return true;
            };
            kit.Transport.Handler = r => Http.Done(FakeTransport.PathOf(r) == "/player-auth/steam"
                ? Http.Json(200, Http.SignIn("t2", provider: "steam"))
                : Http.Error(401, "token_expired", "Token expired"));
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(1, asked);
            Assert.AreEqual(2, kit.Transport.To("GET", "/store/me").Count);
        }

        [TestMethod]
        public async Task GuestSession_RenewsWithTheDeviceSecret()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_X", provider: "device")));
            await kit.Sv.Auth.SignInAsGuestAsync();
            kit.Transport.Handler = r =>
            {
                if (FakeTransport.PathOf(r) == "/player-auth/device") return Http.Done(Http.Json(200, Http.SignIn("t2", provider: "device")));
                return Http.Done(r.Headers["Authorization"] == "Bearer t2" ? Http.Json(200, Http.Holdings()) : Http.Error(401, "token_expired", "Token expired"));
            };
            await kit.Sv.Store.GetMineAsync();
            var device = kit.Transport.Exactly("POST", "/player-auth/device");
            Assert.AreEqual(1, device.Count);
            Assert.IsTrue(JToken.DeepEquals(JObject.Parse("{\"gameId\":123,\"deviceSecret\":\"svpd1_X\"}"), Kit.Body(device[0])));
        }

        [TestMethod]
        public async Task RefusedDeviceSecret_IsNeverSentAgainByTheSdk()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_X", provider: "device")));
            await kit.Sv.Auth.SignInAsGuestAsync();
            var required = new List<SvSignInRequired>();
            kit.Sv.Auth.SignInRequired += required.Add;
            kit.Transport.Handler = r => Http.Done(FakeTransport.PathOf(r) == "/player-auth/device"
                ? Http.Error(401, "proof_invalid")
                : Http.Error(401, "token_expired", "Token expired"));
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(1, kit.Transport.Exactly("POST", "/player-auth/device").Count);
            Assert.AreEqual(1, required.Count);
            Assert.AreEqual(SvSignInRequiredReason.DeviceSecretRejected, required[0].Reason);
            Assert.AreEqual("svpd1_X", kit.Recording.GuestSecret(), "the only copy is kept; the game decides");
        }

        [TestMethod]
        public async Task TokenInvalid_EndsTheSession()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            var required = new List<SvSignInRequired>();
            kit.Sv.Auth.SignInRequired += required.Add;
            kit.Transport.Handler = r => Http.Done(Http.Error(401, "token_invalid"));
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.IsFalse(kit.Sv.Auth.IsSignedIn);
            Assert.AreEqual(SvSignInRequiredReason.TokenInvalid, required.Single().Reason);
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(SvApiException.NotSignedInCode, e.ErrorCode);
        }

        [TestMethod]
        public async Task OtherUnauthorizedCodes_DoNotEndTheSession()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(401, "invalid_credentials"));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Auth.ChangePasswordAsync("wrong", "new password 1"));
            Assert.AreEqual("invalid_credentials", e.ErrorCode);
            Assert.IsTrue(kit.Sv.Auth.IsSignedIn);
            Assert.AreEqual(0, kit.Transport.To("POST", "/player-auth/refresh").Count);
        }
    }

    [TestClass]
    public class AuthFlowTests
    {
        [TestMethod]
        public async Task NewGuest_SavesTheDeviceSecretBeforeTheTokenIsUsed()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(FakeTransport.PathOf(r) == "/player-auth/device/new"
                ? Http.Json(200, Http.SignIn("t1", device: "svpd1_X", provider: "device"))
                : Http.Json(200, Http.Holdings()));
            var result = await kit.Sv.Auth.SignInAsGuestAsync("Nova");
            Assert.IsTrue(result.Renewable);
            Assert.IsTrue(JToken.DeepEquals(JObject.Parse("{\"gameId\":123,\"displayName\":\"Nova\"}"),
                Kit.Body(kit.Transport.To("POST", "/player-auth/device/new")[0])));
            await kit.Sv.Store.GetMineAsync();
            var events = kit.Transport.Events;
            Assert.IsTrue(events.FindIndex(e => e.StartsWith("store set device_secret=") && e.Contains("svpd1_X")) < events.FindIndex(e => e.EndsWith("Bearer t1")), string.Join("\n", events));
            Assert.AreEqual("0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b", kit.Recording.GuestOwner(), "kept with the player it belongs to");
            Assert.IsTrue(await kit.Sv.Auth.HasGuestOnDeviceAsync());
        }

        [TestMethod]
        public async Task SecondGuest_IsRefusedUnlessAskedFor()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_X", provider: "device")));
            await kit.Sv.Auth.CreateGuestAsync();
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => kit.Sv.Auth.CreateGuestAsync());
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t9", device: "svpd1_Y", provider: "device")));
            await kit.Sv.Auth.CreateGuestAsync(replaceExistingGuest: true);
            Assert.AreEqual("svpd1_Y", kit.Recording.GuestSecret());
        }

        [TestMethod]
        public async Task ProofBodies_MatchTheApi()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", provider: FakeTransport.PathOf(r).Split('/').Last())));
            await kit.Sv.Auth.SignInWithMetaAsync(76561198000000001UL, "nonce-1");
            await kit.Sv.Auth.SignInWithSteamAsync(new byte[] { 0x01, 0xAB, 0xFF, 0x00 }, 3);
            await kit.Sv.Auth.SignInWithXsollaAsync("xsolla-jwt", appId: "301");
            await kit.Sv.Auth.SignInWithPlayFabAsync("TICKET");
            await kit.Sv.Auth.SignInWithSupabaseAsync("sb-jwt");
            var bodies = kit.Transport.Requests.Select(r => FakeTransport.PathOf(r) + " " + r.Body).ToList();
            CollectionAssert.AreEqual(new[]
            {
                "/player-auth/meta {\"gameId\":123,\"userId\":\"76561198000000001\",\"nonce\":\"nonce-1\"}",
                "/player-auth/steam {\"gameId\":123,\"ticket\":\"01ABFF\"}",
                "/player-auth/xsolla {\"gameId\":123,\"appId\":\"301\",\"token\":\"xsolla-jwt\"}",
                "/player-auth/playfab {\"gameId\":123,\"sessionTicket\":\"TICKET\"}",
                "/player-auth/supabase {\"gameId\":123,\"token\":\"sb-jwt\"}"
            }, bodies);
            Assert.IsTrue(kit.Transport.Requests.All(r => !r.Headers.ContainsKey("Authorization")), "exchanges carry no credential");
        }

        [TestMethod]
        public async Task GameKey_NamesTheGameInstead()
        {
            var kit = new Kit(o =>
            {
                o.GameId = 0;
                o.GameKey = "sys_1774636058786_30e0fc4d";
            });
            kit.Transport.Handler = r => Http.Done(Http.Json(202, JObject.Parse("{\"expiresInSeconds\":600,\"resendAfterSeconds\":60}")));
            var sent = await kit.Sv.Auth.RequestEmailCodeAsync("nova@example.com");
            Assert.AreEqual(600, sent.ExpiresInSeconds);
            Assert.AreEqual("{\"gameKey\":\"sys_1774636058786_30e0fc4d\",\"email\":\"nova@example.com\"}", kit.Transport.Requests[0].Body);
        }

        [TestMethod]
        public void MetaUserId_MustBeDigits()
        {
            var kit = new Kit();
            Assert.ThrowsException<ArgumentException>(() => kit.Sv.Auth.SignInWithMetaAsync("7.6e16", "n"));
            Assert.ThrowsException<ArgumentException>(() => kit.Sv.Auth.SignInWithSteamAsync("not hex"));
            Assert.AreEqual(0, kit.Transport.Requests.Count);
        }

        [TestMethod]
        public async Task VerifyCode_SendsOnlyGivenFields_AndKeepsTheRefreshToken()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", refresh: "svpr1_A")));
            var result = await kit.Sv.Auth.VerifyEmailCodeAsync("nova@example.com", "12345678");
            Assert.AreEqual("{\"gameId\":123,\"email\":\"nova@example.com\",\"code\":\"12345678\"}", kit.Transport.Requests[0].Body);
            Assert.IsTrue(result.Renewable);
            Assert.AreEqual("svpr1_A", kit.Recording.RefreshToken());
            Assert.AreEqual(SvProviders.Email, kit.Sv.Auth.Method);
        }

        [TestMethod]
        public async Task SigningInAnotherWay_EndsTheStoredEmailSignIn()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            kit.Transport.Handler = r => Http.Done(FakeTransport.PathOf(r) == "/player-auth/sign-out"
                ? new SvHttpResponse(204, "")
                : Http.Json(200, Http.SignIn("t5", provider: "steam")));
            await kit.Sv.Auth.SignInWithSteamAsync("AB");
            await Task.Delay(20); // the server sign-out runs in the background
            Assert.IsNull(kit.Recording.RefreshToken());
            Assert.AreEqual("{\"refreshToken\":\"svpr1_A\"}", kit.Transport.To("POST", "/player-auth/sign-out").Single().Body);
        }

        [TestMethod]
        public async Task Restore_FollowsTheLastWayIn()
        {
            var empty = new Kit();
            Assert.IsFalse(await empty.Sv.Auth.RestoreAsync());
            Assert.AreEqual(0, empty.Transport.Requests.Count);

            var store = new SvMemorySecureStore();
            var first = new Kit(store: store);
            first.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_X", provider: "device")));
            await first.Sv.Auth.SignInAsGuestAsync();

            var guest = new Kit(store: store); // a new launch: same storage, nothing in memory
            guest.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t2", provider: "device")));
            Assert.IsTrue(await guest.Sv.Auth.RestoreAsync());
            Assert.AreEqual("/player-auth/device", FakeTransport.PathOf(guest.Transport.Requests.Single()));

            var email = new Kit();
            await email.SignInByEmailAsync();
            var relaunch = new Kit(store: email.Store);
            relaunch.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t3", refresh: "svpr1_B")));
            Assert.IsTrue(await relaunch.Sv.Auth.RestoreAsync());
            Assert.AreEqual("/player-auth/refresh", FakeTransport.PathOf(relaunch.Transport.Requests.Single()));
            Assert.AreEqual("t3", relaunch.Sv.Auth.PlayerToken);
        }

        [TestMethod]
        public async Task Restore_NeverSwapsInTheGuest_AfterAPlatformSignIn()
        {
            var store = new SvMemorySecureStore();
            var first = new Kit(store: store);
            first.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_X", provider: "device",
                playerId: "aaaaaaaa-0000-4000-8000-000000000001")));
            await first.Sv.Auth.SignInAsGuestAsync();
            await first.SignInBySteamAsync("t2"); // another player, signed in with Steam

            var relaunch = new Kit(store: store);
            Assert.IsFalse(await relaunch.Sv.Auth.RestoreAsync(), "the guest is a different player than the last sign-in");
            Assert.AreEqual(0, relaunch.Transport.Requests.Count);
            Assert.AreEqual(SvProviders.Steam, await relaunch.Sv.Auth.GetLastSignInMethodAsync());
            Assert.IsTrue(await relaunch.Sv.Auth.HasGuestOnDeviceAsync(), "the guest is still there for an explicit SignInAsGuestAsync");
        }

        [TestMethod]
        public async Task Restore_AfterSignOut_IsFalse()
        {
            var store = new SvMemorySecureStore();
            var first = new Kit(store: store);
            first.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_X", provider: "device")));
            await first.Sv.Auth.SignInAsGuestAsync();
            await first.Sv.Auth.SignOutAsync();
            var relaunch = new Kit(store: store);
            Assert.IsFalse(await relaunch.Sv.Auth.RestoreAsync());
            Assert.IsNull(await relaunch.Sv.Auth.GetLastSignInMethodAsync());
            Assert.AreEqual(0, relaunch.Transport.Requests.Count);
        }

        [TestMethod]
        public async Task Restore_Offline_ThrowsInsteadOfSayingSignedOut()
        {
            var email = new Kit();
            await email.SignInByEmailAsync();
            var relaunch = new Kit(store: email.Store);
            relaunch.Transport.Handler = r => Http.NetworkFailure();
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => relaunch.Sv.Auth.RestoreAsync());
            Assert.IsTrue(e.IsNetworkError);
            Assert.AreEqual("svpr1_A", email.Recording.RefreshToken(), "kept for the next try");
        }

        [TestMethod]
        public async Task SignOut_EndsTheEmailSignIn_AndKeepsTheGuestSecret()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_X", provider: "device")));
            await kit.Sv.Auth.SignInAsGuestAsync();
            await kit.Sv.Auth.SignOutAsync();
            Assert.IsFalse(kit.Sv.Auth.IsSignedIn);
            Assert.AreEqual("svpd1_X", kit.Recording.GuestSecret());

            var email = new Kit();
            await email.SignInByEmailAsync();
            email.Transport.Handler = r => Http.Done(new SvHttpResponse(204, ""));
            await email.Sv.Auth.SignOutAsync();
            Assert.AreEqual("{\"refreshToken\":\"svpr1_A\"}", email.Transport.To("POST", "/player-auth/sign-out").Single().Body);
            Assert.IsNull(email.Recording.RefreshToken());
        }

        private const string ProfileWithoutEmail =
            "{\"player\":{\"id\":\"0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b\",\"displayName\":null,\"createdAt\":\"2026-11-01T00:00:00.000Z\",\"lastSignedInAt\":null}," +
            "\"game\":{\"gameKey\":\"k\"},\"identities\":[{\"provider\":\"steam\",\"linkedAt\":\"2026-11-02T00:00:00.000Z\"}]}";

        [TestMethod]
        public async Task RemoveEmail_IsAPostWithAJsonBody_AndEndsTheEmailSignIn()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, JObject.Parse(ProfileWithoutEmail)));
            var profile = await kit.Sv.Auth.RemoveEmailAsync(currentPassword: "correct horse");
            var request = kit.Transport.Requests.Last();
            Assert.AreEqual("POST", request.Method);
            Assert.AreEqual("/player-auth/link/email/remove", FakeTransport.PathOf(request));
            Assert.AreEqual("{\"currentPassword\":\"correct horse\"}", request.Body);
            Assert.AreEqual("application/json", request.Headers["Content-Type"]);
            Assert.AreEqual(0, kit.Transport.To("DELETE", "/player-auth/link/email").Count, "no DELETE when the POST form exists");
            Assert.IsFalse(profile.HasIdentity(SvProviders.Email));
            Assert.IsNull(kit.Recording.RefreshToken());
        }

        [TestMethod]
        public async Task RemoveEmail_OnAnOlderApi_FallsBackToTheDeleteOnce()
        {
            // An older API has no POST /link/email/remove: a 404, as not_found (studio error shape), as the framework's
            // { name: NOT_FOUND } (APIs before that shape), or with no JSON at all (a proxy's page).
            var missing = new[]
            {
                Http.Error(404, "not_found", "No such route. Check the method and the path."),
                Http.Json(404, JObject.Parse("{\"name\":\"NOT_FOUND\",\"message\":\"Resource \\\"/rest/player-auth/link/email/remove\\\" not found\",\"status\":404,\"errors\":[]}")),
                new SvHttpResponse(404, "<html>Not Found</html>")
            };
            foreach (var notThere in missing)
            {
                var kit = new Kit();
                await kit.SignInByEmailAsync();
                kit.Transport.Handler = r => Http.Done(r.Method == "POST" && FakeTransport.PathOf(r) == "/player-auth/link/email/remove"
                    ? notThere
                    : Http.Json(200, JObject.Parse(ProfileWithoutEmail)));
                var profile = await kit.Sv.Auth.RemoveEmailAsync(oldCode: "33334444");
                var post = kit.Transport.Exactly("POST", "/player-auth/link/email/remove");
                var delete = kit.Transport.Exactly("DELETE", "/player-auth/link/email");
                Assert.AreEqual(1, post.Count);
                Assert.AreEqual(1, delete.Count, "the DELETE is sent once");
                Assert.AreEqual("{\"oldCode\":\"33334444\"}", delete[0].Body, "the same body");
                Assert.AreEqual("application/json", delete[0].Headers["Content-Type"]);
                Assert.IsFalse(profile.HasIdentity(SvProviders.Email));
                Assert.IsNull(kit.Recording.RefreshToken());
            }
        }

        [TestMethod]
        public async Task RemoveEmail_NeverFallsBackOnARealRefusal_AndSendsTheDeleteAtMostOnce()
        {
            // A refusal of the POST itself (wrong password) is the answer: no DELETE.
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(401, "invalid_credentials"));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Auth.RemoveEmailAsync(currentPassword: "wrong"));
            Assert.AreEqual("invalid_credentials", e.ErrorCode);
            Assert.AreEqual(0, kit.Transport.To("DELETE", "/player-auth/link/email").Count);
            Assert.IsNotNull(kit.Recording.RefreshToken(), "nothing was removed: the sign-in stays");

            // A 404 with another error code is not a missing route.
            kit.Transport.Handler = r => Http.Done(Http.Error(404, "player_not_found"));
            e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Auth.RemoveEmailAsync(currentPassword: "correct horse"));
            Assert.AreEqual("player_not_found", e.ErrorCode);
            Assert.AreEqual(0, kit.Transport.To("DELETE", "/player-auth/link/email").Count);

            // Both forms missing: one POST, one DELETE, then the DELETE's 404 is thrown.
            kit.Transport.Handler = r => Http.Done(Http.Error(404, "not_found"));
            var before = kit.Transport.Requests.Count;
            e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Auth.RemoveEmailAsync(currentPassword: "correct horse"));
            Assert.AreEqual(404, e.Status);
            var sent = kit.Transport.Requests.Skip(before).Select(r => r.Method + " " + FakeTransport.PathOf(r)).ToList();
            CollectionAssert.AreEqual(new[] { "POST /player-auth/link/email/remove", "DELETE /player-auth/link/email" }, sent);
        }

        [TestMethod]
        public async Task ChangeEmail_AndChangePassword_SaveTheNewRefreshToken()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync();
            kit.Transport.Handler = r => Http.Done(FakeTransport.PathOf(r) == "/player-auth/me/password"
                ? Http.Json(200, JObject.Parse("{\"refreshToken\":\"svpr1_P\",\"refreshExpiresAt\":\"2026-12-14T12:00:00.000Z\"}"))
                : Http.Json(200, JObject.Parse(
                    "{\"player\":{\"id\":\"0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b\"},\"game\":{\"gameKey\":\"k\"},\"identities\":[{\"provider\":\"email\",\"linkedAt\":\"x\"}]," +
                    "\"refreshToken\":\"svpr1_E\",\"refreshExpiresAt\":\"2026-12-14T12:00:00.000Z\"}")));
            await kit.Sv.Auth.ChangeEmailAsync("new@example.com", "11112222", oldCode: "33334444");
            Assert.AreEqual("{\"email\":\"new@example.com\",\"code\":\"11112222\",\"oldCode\":\"33334444\"}", kit.Transport.Requests.Last().Body);
            Assert.AreEqual("svpr1_E", kit.Recording.RefreshToken());
            await kit.Sv.Auth.ChangePasswordAsync("correct horse", "battery staple");
            Assert.AreEqual("PUT", kit.Transport.Requests.Last().Method);
            Assert.AreEqual("{\"currentPassword\":\"correct horse\",\"newPassword\":\"battery staple\"}", kit.Transport.Requests.Last().Body);
            Assert.AreEqual("svpr1_P", kit.Recording.RefreshToken());
        }

        [TestMethod]
        public async Task EmailRemovalCode_AndLinkCode_Bodies()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(Http.Json(202, JObject.Parse("{\"expiresInSeconds\":600,\"resendAfterSeconds\":60}")));
            await kit.Sv.Auth.RequestLinkEmailCodeAsync("nova@example.com");
            await kit.Sv.Auth.RequestEmailRemovalCodeAsync();
            var bodies = kit.Transport.To("POST", "/player-auth/link/email/code").Select(r => r.Body).ToList();
            CollectionAssert.AreEqual(new[] { "{\"email\":\"nova@example.com\"}", "{\"unlink\":true}" }, bodies);
        }

        [TestMethod]
        public async Task UnlinkDevice_ForgetsTheLocalSecret()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_X", provider: "device")));
            await kit.Sv.Auth.SignInAsGuestAsync();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, JObject.Parse("{\"player\":{\"id\":\"p\"},\"game\":{\"gameKey\":\"k\"},\"identities\":[]}")));
            await kit.Sv.Auth.UnlinkAsync(SvProviders.Device);
            Assert.AreEqual("DELETE", kit.Transport.Requests.Last().Method);
            Assert.AreEqual("/player-auth/link/device", FakeTransport.PathOf(kit.Transport.Requests.Last()));
            Assert.IsNull(kit.Transport.Requests.Last().Body);
            Assert.IsFalse(await kit.Sv.Auth.HasGuestOnDeviceAsync());
            Assert.ThrowsException<ArgumentException>(() => kit.Sv.Auth.UnlinkAsync(SvProviders.Studio).GetAwaiter().GetResult());
        }

        [TestMethod]
        public async Task EraseMe_ClearsEverything_AndAnAlreadyErasedPlayerIsDone()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t1", device: "svpd1_X", provider: "device")));
            await kit.Sv.Auth.SignInAsGuestAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(401, "player_deleted"));
            await kit.Sv.Auth.EraseMeAsync();
            Assert.IsFalse(kit.Sv.Auth.IsSignedIn);
            Assert.IsNull(kit.Recording.GuestSecret());
        }

        [TestMethod]
        public async Task PlayerRoutes_WithoutASignIn_SendNothing()
        {
            var kit = new Kit();
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Data.GetMineAsync());
            Assert.AreEqual(SvApiException.NotSignedInCode, e.ErrorCode);
            Assert.AreEqual(0, e.Status);
            Assert.AreEqual(0, kit.Transport.Requests.Count);
        }

        [TestMethod]
        public async Task StudioToken_IsCheckedWithMe()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Json(200, JObject.Parse(
                "{\"player\":{\"id\":\"0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b\",\"displayName\":\"Nova\"},\"game\":{\"gameKey\":\"k\"},\"identities\":[{\"provider\":\"studio\",\"linkedAt\":\"x\"}]}")));
            // header.payload.signature with payload { sub, amr: ["studio"], exp }
            var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                "{\"sub\":\"0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b\",\"amr\":[\"studio\"],\"exp\":1794657600}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var me = await kit.Sv.Auth.SignInWithStudioTokenAsync("eyJhbGciOiJFZERTQSJ9." + payload + ".sig");
            Assert.AreEqual("Nova", me.Player.DisplayName);
            Assert.AreEqual(SvProviders.Studio, kit.Sv.Auth.Method);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1794657600), kit.Sv.Auth.TokenExpiresAtUtc);
            Assert.AreEqual("/player-auth/me", FakeTransport.PathOf(kit.Transport.Requests.Single()));
        }

        [TestMethod]
        public void Construction_RefusesKeysOfBothKinds_InsecureUrls_AndUnsupportedPlatforms()
        {
            Assert.ThrowsException<ArgumentException>(() => new SvPlayers(new SvPlayersOptions { GameId = 1, GameKey = "k", Transport = new FakeTransport() }));
            Assert.ThrowsException<ArgumentException>(() => new SvPlayers(new SvPlayersOptions { Transport = new FakeTransport() }));
            Assert.ThrowsException<ArgumentException>(() =>
                new SvPlayers(new SvPlayersOptions { GameId = 1, BaseUrl = "http://api.example.com/rest", Transport = new FakeTransport() }));
            Assert.IsNotNull(new SvPlayers(new SvPlayersOptions { GameId = 1, BaseUrl = "http://localhost:3000/rest", Transport = new FakeTransport() }));

            var before = SvPlayersDefaults.UnsupportedPlatformMessage;
            try
            {
                SvPlayersDefaults.UnsupportedPlatformMessage = "WebGL: CORS";
                var e = Assert.ThrowsException<PlatformNotSupportedException>(() => new SvPlayers(7));
                Assert.AreEqual("WebGL: CORS", e.Message);
            }
            finally
            {
                SvPlayersDefaults.UnsupportedPlatformMessage = before;
            }
        }
    }
}
