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
    public class ErrorParsingTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");

        [TestMethod]
        public void StudioShape_ReadsCodeMessageAndDetails()
        {
            // PlayerDataError.toBody: { error, message, details }
            var r = Http.Json(409, JObject.Parse(
                "{\"error\":\"version_conflict\",\"message\":\"The data changed since it was read (ifVersion did not match). Read it again and retry.\"," +
                "\"details\":[{\"store\":\"player\",\"key\":\"settings\",\"version\":7}]}"));
            var e = SvApiException.FromResponse(r, Now);
            Assert.AreEqual(409, e.Status);
            Assert.AreEqual("version_conflict", e.ErrorCode);
            Assert.IsTrue(e.Is("version_conflict"));
            StringAssert.StartsWith(e.Message, "The data changed");
            var details = e.DetailsAs<List<SvVersionInfo>>();
            Assert.AreEqual(1, details.Count);
            Assert.AreEqual("settings", details[0].Key);
            Assert.AreEqual(7L, details[0].Version);
            Assert.IsNull(e.ErrorName);
            Assert.IsFalse(e.IsTransient);
        }

        [TestMethod]
        public void TsedShape_ReadsNameMessageAndErrors_WithNoCode()
        {
            var r = Http.Json(404, JObject.Parse("{\"name\":\"NOT_FOUND\",\"message\":\"Resource \\\"/rest/nope\\\" not found\",\"status\":404,\"errors\":[]}"));
            var e = SvApiException.FromResponse(r, Now);
            Assert.AreEqual(404, e.Status);
            Assert.IsNull(e.ErrorCode);
            Assert.AreEqual("NOT_FOUND", e.ErrorName);
            StringAssert.Contains(e.Message, "not found");
            Assert.AreEqual(JTokenType.Array, e.Errors.Type);
        }

        [TestMethod]
        public void HtmlBody_IsNeverQuoted_AndStatusDecides()
        {
            var e = SvApiException.FromResponse(new SvHttpResponse(502, "<html><body>Bad gateway token=secret</body></html>"), Now);
            Assert.AreEqual("HTTP 502", e.Message);
            Assert.IsNull(e.ErrorCode);
            Assert.IsTrue(e.IsTransient);
        }

        [TestMethod]
        public void RetryAfter_SecondsOrHttpDate()
        {
            Assert.AreEqual(TimeSpan.FromSeconds(60), SvApiException.ParseRetryAfter("60", Now));
            Assert.AreEqual(TimeSpan.FromSeconds(30), SvApiException.ParseRetryAfter(Now.AddSeconds(30).ToString("r"), Now));
            Assert.AreEqual(TimeSpan.Zero, SvApiException.ParseRetryAfter(Now.AddSeconds(-5).ToString("r"), Now));
            Assert.IsNull(SvApiException.ParseRetryAfter("soon", Now));
            Assert.IsNull(SvApiException.ParseRetryAfter(null, Now));
        }

        [TestMethod]
        public void RateLimited_CarriesRetryAfter()
        {
            var e = SvApiException.FromResponse(Http.Error(429, "rate_limited", "Too many requests.", ("Retry-After", "60")), Now);
            Assert.AreEqual("rate_limited", e.ErrorCode);
            Assert.AreEqual(TimeSpan.FromSeconds(60), e.RetryAfter);
            Assert.IsTrue(e.IsTransient);
        }

        [TestMethod]
        public void DataBusy_IsTransientWithItsOwnRetryAfter()
        {
            var e = SvApiException.FromResponse(Http.Error(503, "data_busy", "busy", ("retry-after", "1")), Now);
            Assert.AreEqual(TimeSpan.FromSeconds(1), e.RetryAfter);
            Assert.IsTrue(e.IsTransient);
        }

        [TestMethod]
        public void TransientStatuses()
        {
            foreach (var s in new[] { 408, 429, 500, 502, 503, 504 }) Assert.IsTrue(SvApiException.IsTransientStatus(s), s.ToString());
            foreach (var s in new[] { 200, 304, 400, 401, 403, 404, 409, 413, 422 }) Assert.IsFalse(SvApiException.IsTransientStatus(s), s.ToString());
        }

        [TestMethod]
        public void TransportFailures_BecomeSdkCodesWithStatusZero()
        {
            var net = SvApiException.FromTransport(new SvTransportException(SvTransportFailure.Network, "reset"));
            var slow = SvApiException.FromTransport(new SvTransportException(SvTransportFailure.Timeout, "timeout"));
            Assert.AreEqual(0, net.Status);
            Assert.AreEqual(SvApiException.NetworkErrorCode, net.ErrorCode);
            Assert.AreEqual(SvApiException.TimeoutCode, slow.ErrorCode);
            Assert.IsTrue(net.IsNetworkError && net.IsTransient);
            Assert.IsTrue(slow.IsNetworkError && slow.IsTransient);
        }
    }

    [TestClass]
    public class RetryTests
    {
        [TestMethod]
        public async Task Get_IsRepeatedAfterATransientFailure()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            var calls = 0;
            kit.Transport.Handler = r => Http.Done(++calls == 1 ? Http.Error(503, null, "x") : Http.Json(200, Http.Holdings()));
            var mine = await kit.Sv.Store.GetMineAsync();
            Assert.AreEqual(700, mine.BalanceOf("GOLD"));
            Assert.AreEqual(2, kit.Transport.To("GET", "/store/me").Count);
            CollectionAssert.AreEqual(new[] { TimeSpan.FromMilliseconds(500) }, kit.Waits);
        }

        [TestMethod]
        public async Task RetryAfter_IsWaitedOutExactly()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            var calls = 0;
            kit.Transport.Handler = r => Http.Done(++calls == 1 ? Http.Error(429, "rate_limited", "x", ("Retry-After", "3")) : Http.Json(200, Http.Holdings()));
            await kit.Sv.Store.GetMineAsync();
            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(3) }, kit.Waits);
        }

        [TestMethod]
        public async Task LongRetryAfter_IsNotWaitedOut_TheGameGetsIt()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(429, "rate_limited", "x", ("Retry-After", "60")));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(TimeSpan.FromSeconds(60), e.RetryAfter);
            Assert.AreEqual(1, kit.Transport.To("GET", "/store/me").Count);
            Assert.AreEqual(0, kit.Waits.Count);
        }

        [TestMethod]
        public async Task GivesUpAfterMaxAttempts_WithBackoff()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(502, "provider_unavailable"));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetCatalogAsync());
            Assert.AreEqual(502, e.Status);
            Assert.AreEqual(3, kit.Transport.To("GET", "/store/catalog").Count);
            CollectionAssert.AreEqual(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1) }, kit.Waits);
        }

        [TestMethod]
        public async Task ClientErrors_AreNeverRepeated()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(400, "bad_request"));
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetHistoryAsync("not-a-txn"));
            Assert.AreEqual(1, kit.Transport.To("GET", "/store/me/history").Count);
        }

        [TestMethod]
        public async Task PlatformSignIn_IsNeverRepeated_EvenAfterANetworkFailure()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.NetworkFailure();
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Auth.SignInWithMetaAsync("1234567890", "nonce-1"));
            Assert.AreEqual(SvApiException.NetworkErrorCode, e.ErrorCode);
            Assert.AreEqual(1, kit.Transport.Requests.Count);
        }

        [TestMethod]
        public async Task ProofInvalid_IsNeverRepeated()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(Http.Error(401, "proof_invalid"));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Auth.SignInWithSupabaseAsync("jwt"));
            Assert.AreEqual("proof_invalid", e.ErrorCode);
            Assert.AreEqual(1, kit.Transport.Requests.Count);
            Assert.IsFalse(kit.Sv.Auth.IsSignedIn);
        }

        [TestMethod]
        public async Task DataWrite_WithoutIfVersion_IsNotRepeated()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Data.WriteAsync(new SvDataOps().Inc("matches", 1)));
            Assert.AreEqual(1, kit.Transport.To("PATCH", "/player-data/me").Count);
        }

        [TestMethod]
        public async Task DataWrite_WithIfVersionOnEveryOp_IsRepeatedOnlyAfterAnAnswerThatProvesNothingApplied()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            var calls = 0;
            kit.Transport.Handler = r => ++calls == 1
                ? Http.Done(Http.Error(503, "data_busy", "busy"))
                : Http.Done(Http.Json(200, JObject.Parse("{\"results\":[{\"store\":\"player\",\"key\":\"settings\",\"version\":8}]}")));
            var result = await kit.Sv.Data.WriteAsync(new SvDataOps().Set("settings", new { fov = 90 }, ifVersion: 7));
            Assert.AreEqual(8L, result.Results[0].Version);
            var writes = kit.Transport.To("PATCH", "/player-data/me");
            Assert.AreEqual(2, writes.Count);
            Assert.AreEqual(writes[0].Body, writes[1].Body);
        }

        [TestMethod]
        public async Task DataWrite_MixedIfVersion_IsNotRepeated()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(503, "data_busy", "busy", ("Retry-After", "1")));
            var ops = new SvDataOps().Set("a", 1, ifVersion: 3).Set("b", 2);
            Assert.IsFalse(ops.AllHaveIfVersion);
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Data.WriteAsync(ops));
            Assert.AreEqual(1, kit.Transport.To("PATCH", "/player-data/me").Count);
        }

        [TestMethod]
        public async Task MetaPurchaseCheck_GuestSignIn_AndSignOut_AreNotRepeated()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(503, "platform_not_available"));
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.SyncMetaPurchasesAsync());
            Assert.AreEqual(1, kit.Transport.To("POST", "/store/me/platform-purchases/meta").Count, "each check spends one of 6 a minute");

            var guest = new Kit();
            await guest.Store.SetAsync(guest.Sv.StoragePrefix + "device_secret", "svpd1_X");
            guest.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => guest.Sv.Auth.SignInWithDeviceAsync());
            Assert.AreEqual(1, guest.Transport.Requests.Count);
        }

        [TestMethod]
        public void Policy_NeverRetriesNonTransient_AndStopsAtMax()
        {
            var policy = new SvRetryPolicy { Jitter = 0 };
            Assert.IsFalse(policy.ShouldRetry(new SvApiException(409, "insufficient_funds", "x"), 1, out _));
            Assert.IsTrue(policy.ShouldRetry(new SvApiException(503, null, "x"), 1, out var d1));
            Assert.AreEqual(TimeSpan.FromMilliseconds(500), d1);
            Assert.IsTrue(policy.ShouldRetry(new SvApiException(503, null, "x"), 2, out var d2));
            Assert.AreEqual(TimeSpan.FromSeconds(1), d2);
            Assert.IsFalse(policy.ShouldRetry(new SvApiException(503, null, "x"), 3, out _));
            Assert.IsFalse(SvRetryPolicy.None.ShouldRetry(new SvApiException(503, null, "x"), 1, out _));
        }

        [TestMethod]
        public void Policy_JitterNeverShortensRetryAfter()
        {
            var policy = new SvRetryPolicy { Jitter = 1 };
            for (var i = 0; i < 50; i++)
            {
                Assert.IsTrue(policy.ShouldRetry(new SvApiException(429, "rate_limited", "x", retryAfter: TimeSpan.FromSeconds(2)), 1, out var d));
                Assert.AreEqual(TimeSpan.FromSeconds(2), d);
                var backoff = policy.Backoff(1);
                Assert.IsTrue(backoff >= TimeSpan.FromMilliseconds(250) && backoff <= TimeSpan.FromMilliseconds(500), backoff.ToString());
            }
        }

        [TestMethod]
        public async Task NoRequest_CarriesAnApiKey_AndBodiesAreJson()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(FakeAnswers.For(r));
            await kit.Sv.Store.GetCatalogAsync();
            await kit.Sv.Data.WriteAsync(new SvDataOps().Set("k", 1));
            await kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500);
            foreach (var r in kit.Transport.Requests)
            {
                Assert.IsFalse(r.Headers.ContainsKey("X-API-Key"), r.Url);
                Assert.AreEqual("application/json", r.Headers["Accept"]);
                if (r.Body != null) Assert.AreEqual("application/json", r.Headers["Content-Type"], r.Url);
                else Assert.IsFalse(r.Headers.ContainsKey("Content-Type"), r.Url);
            }
            Assert.AreEqual("Bearer t1", kit.Transport.To("GET", "/store/catalog")[0].Headers["Authorization"]);
        }
    }

    internal static class FakeAnswers
    {
        public static SvHttpResponse For(SvHttpRequest r)
        {
            var path = FakeTransport.PathOf(r);
            if (path.StartsWith("/store/catalog"))
                return Http.Json(200, JObject.Parse("{\"settings\":{\"playerPurchases\":true,\"playerConsume\":true},\"currencies\":[],\"items\":[]}"));
            if (path.StartsWith("/store/me/purchases") || path.StartsWith("/store/me/consume")) return Http.Json(201, Http.Txn(false));
            if (path.StartsWith("/player-data/me") && r.Method == "PATCH")
                return Http.Json(200, JObject.Parse("{\"results\":[{\"store\":\"player\",\"key\":\"k\",\"version\":1}]}"));
            if (path.StartsWith("/store/me")) return Http.Json(200, Http.Holdings());
            return Http.Error(404, "not_set_up");
        }
    }
}
