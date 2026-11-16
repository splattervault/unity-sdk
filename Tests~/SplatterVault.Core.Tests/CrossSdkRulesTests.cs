using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SplatterVault.Players;

namespace SplatterVault.Core.Tests
{
    /// <summary>Rule 4 of the cross-SDK rules (2026-11-15): pending store changes belong to one player.</summary>
    [TestClass]
    public class PendingPerPlayerTests
    {
        private const string PlayerA = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b";
        private const string PlayerB = "bbbbbbbb-0000-4000-8000-000000000002";

        /// <summary>Two of A's purchases are left unanswered (kept in the pending queue).</summary>
        private static async Task<Kit> TwoPendingForAAsync()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync("tA");
            kit.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500, idempotencyKey: "a-1"));
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500, idempotencyKey: "a-2"));
            Assert.AreEqual(2, (await kit.Sv.Store.GetPendingAsync()).Count);
            return kit;
        }

        private static List<SvPendingStoreChange> Kept(Kit kit)
        {
            var raw = kit.Recording.Get("pending_store_changes");
            return raw == null ? new List<SvPendingStoreChange>() : SvJson.Deserialize<List<SvPendingStoreChange>>(raw);
        }

        [TestMethod]
        public async Task Resume_StopsWhenAnotherPlayerSignsInMidway_AndNeverSendsAsThem()
        {
            var kit = await TwoPendingForAAsync();
            var before = kit.Transport.Requests.Count;
            var switched = false;
            kit.Transport.Handler = async r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/steam") return Http.Json(200, Http.SignIn("tB", provider: "steam", playerId: PlayerB));
                if (path == "/store/me/purchases" && !switched)
                {
                    switched = true;
                    // While A's first change is in flight, player B signs in on this device.
                    await kit.Sv.Auth.SignInWithSteamAsync("CD");
                }
                return Http.Json(200, Http.Txn(true));
            };
            var finished = await kit.Sv.Store.ResumePendingAsync();

            var sends = kit.Transport.Requests.Skip(before).Where(r => FakeTransport.PathOf(r) == "/store/me/purchases").ToList();
            Assert.IsTrue(sends.All(s => s.Headers["Authorization"] == "Bearer tA"),
                "a change of A was sent with another player's token: " + string.Join(",", sends.Select(s => s.Headers["Authorization"])));
            Assert.AreEqual(1, sends.Count, "resume stops at the player change");
            Assert.AreEqual(1, finished.Count);
            // B sees none of A's changes; A's unsent change is still kept for A.
            Assert.AreEqual(0, (await kit.Sv.Store.GetPendingAsync()).Count);
            Assert.AreEqual("a-2", Kept(kit).Single().IdempotencyKey);
            Assert.AreEqual(PlayerA, Kept(kit).Single().PlayerId);
        }

        [TestMethod]
        public async Task Resume_SkipsAChangeFinishedMeanwhile()
        {
            var kit = await TwoPendingForAAsync();
            var before = kit.Transport.Requests.Count;
            var first = true;
            kit.Transport.Handler = async r =>
            {
                if (first)
                {
                    first = false;
                    // A concurrent caller finishes (here: discards) a-2 while a-1 is in flight.
                    await kit.Sv.Store.DiscardPendingAsync("a-2");
                }
                return Http.Json(200, Http.Txn(true));
            };
            var finished = await kit.Sv.Store.ResumePendingAsync();
            Assert.AreEqual(1, finished.Count);
            var keys = kit.Transport.Requests.Skip(before).Select(r => r.Headers["Idempotency-Key"]).ToList();
            CollectionAssert.AreEqual(new[] { "a-1" }, keys, "a-2 was no longer pending and must not be sent");
            Assert.AreEqual(0, Kept(kit).Count);
        }

        [TestMethod]
        public async Task SameKey_TwoPlayers_AreKeptAndRemovedSeparately()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync("tA");
            kit.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500, idempotencyKey: "shared-key"));
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("tB", provider: "steam", playerId: PlayerB)));
            await kit.Sv.Auth.SignInWithSteamAsync("CD");
            kit.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500, idempotencyKey: "shared-key"));
            Assert.AreEqual(2, Kept(kit).Count, "B's change with the same key does not replace A's");

            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.Txn(true)));
            var finished = await kit.Sv.Store.ResumePendingAsync();
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual(PlayerA, Kept(kit).Single().PlayerId, "finishing B's change leaves A's");

            await kit.Sv.Store.DiscardPendingAsync("shared-key"); // signed in as B: only B's (none left)
            Assert.AreEqual(PlayerA, Kept(kit).Single().PlayerId);
        }

        [TestMethod]
        public async Task FirstSend_AfterThePlayerChanged_IsNotSent_AndStaysPendingForItsPlayer()
        {
            var store = new HookStore();
            var kit = new Kit(store: store);
            await kit.SignInBySteamAsync("tA");
            kit.Transport.Handler = r => Http.Done(FakeTransport.PathOf(r) == "/player-auth/steam"
                ? Http.Json(200, Http.SignIn("tB", provider: "steam", playerId: PlayerB))
                : Http.Json(201, Http.Txn(false)));
            // B signs in between the commit (the change is kept for A) and the send.
            store.OnFirstPendingWrite = () => kit.Sv.Auth.SignInWithSteamAsync("CD");
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500, idempotencyKey: "a-9"));
            Assert.AreEqual(SvApiException.PlayerChangedCode, e.ErrorCode);
            Assert.AreEqual(0, e.Status);
            Assert.AreEqual("a-9", e.IdempotencyKey);
            Assert.AreEqual(0, kit.Transport.To("POST", "/store/me/purchases").Count, "nothing sent with B's token");
            var kept = SvJson.Deserialize<List<SvPendingStoreChange>>(await store.GetAsync(kit.Sv.StoragePrefix + "pending_store_changes"));
            Assert.AreEqual(PlayerA, kept.Single().PlayerId);
            Assert.AreEqual(0, (await kit.Sv.Store.GetPendingAsync()).Count, "B does not see A's change");
        }

        [TestMethod]
        public async Task DiscardPending_NeedsASignedInPlayer()
        {
            var kit = new Kit();
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.DiscardPendingAsync("k"));
            Assert.AreEqual(SvApiException.NotSignedInCode, e.ErrorCode);
        }

        /// <summary>A store that runs a hook once, right after the first write of the pending list.</summary>
        private sealed class HookStore : ISecureStore
        {
            private readonly ISecureStore _inner;
            public Func<Task> OnFirstPendingWrite;

            public HookStore()
            {
                _inner = new SvMemorySecureStore();
            }

            public Task<string> GetAsync(string key) => _inner.GetAsync(key);

            public async Task SetAsync(string key, string value)
            {
                await _inner.SetAsync(key, value);
                if (key.EndsWith("pending_store_changes") && OnFirstPendingWrite != null)
                {
                    var hook = OnFirstPendingWrite;
                    OnFirstPendingWrite = null;
                    await hook();
                }
            }

            public Task DeleteAsync(string key) => _inner.DeleteAsync(key);
        }
    }

    /// <summary>Rule 2: data writes with ifVersion have an unknown outcome after no answer, 500, 502, 504.</summary>
    [TestClass]
    public class OutcomeUnknownTests
    {
        private static readonly JObject WriteOk = JObject.Parse("{\"results\":[{\"store\":\"player\",\"key\":\"wins\",\"version\":8}]}");

        private static async Task<Kit> SignedInAsync()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            return kit;
        }

        private static SvDataOps IfVersionWrite() => new SvDataOps().Set("wins", 5, ifVersion: 7);

        [DataTestMethod]
        [DataRow(0)] // no answer
        [DataRow(500)]
        [DataRow(502)]
        [DataRow(504)]
        public async Task IfVersionWrite_AfterAnUnknownOutcome_IsNotResent_AndIsFlagged(int status)
        {
            var kit = await SignedInAsync();
            kit.Transport.Handler = r => status == 0 ? Http.NetworkFailure() : Http.Done(Http.Error(status, "internal_error"));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Data.WriteAsync(IfVersionWrite()));
            Assert.IsTrue(e.IsOutcomeUnknown);
            Assert.AreEqual(1, kit.Transport.To("PATCH", "/player-data/me").Count);
        }

        [DataTestMethod]
        [DataRow(408)]
        [DataRow(429)]
        [DataRow(503)]
        public async Task IfVersionWrite_AfterAnAnswerProvingNothingApplied_IsResent(int status)
        {
            var kit = await SignedInAsync();
            var calls = 0;
            kit.Transport.Handler = r => ++calls == 1
                ? Http.Done(Http.Error(status, status == 429 ? "rate_limited" : "data_busy", "busy", ("Retry-After", "1")))
                : Http.Done(Http.Json(200, WriteOk));
            var result = await kit.Sv.Data.WriteAsync(IfVersionWrite());
            Assert.AreEqual(8L, result.Results[0].Version);
            Assert.AreEqual(2, kit.Transport.To("PATCH", "/player-data/me").Count);
        }

        [TestMethod]
        public async Task ResentWrite_AnsweredVersionConflict_IsOutcomeUnknown_NeverAPlainConflict()
        {
            var kit = await SignedInAsync();
            var calls = 0;
            var versions = new JArray(new JObject { ["store"] = "player", ["key"] = "wins", ["version"] = 8 });
            kit.Transport.Handler = r => ++calls == 1
                ? Http.Done(Http.Error(503, "data_busy", "busy"))
                : Http.Done(Http.Json(409, new JObject { ["error"] = "version_conflict", ["message"] = "changed", ["details"] = versions }));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Data.WriteAsync(IfVersionWrite()));
            Assert.IsTrue(e.IsOutcomeUnknown);
            Assert.AreEqual(SvApiException.OutcomeUnknownCode, e.ErrorCode, "never reported as a plain version_conflict");
            Assert.IsFalse(e.Is("version_conflict"));
            Assert.AreEqual(409, e.Status);
            Assert.AreEqual(8L, e.DetailsAs<List<SvVersionInfo>>().Single().Version, "the current versions are kept");
        }

        [TestMethod]
        public async Task ConflictAfterTheTokenRenewalResend_CountsBothSends()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync("t1", "svpr1_A");
            kit.Transport.Handler = r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/refresh") return Http.Done(Http.Json(200, Http.SignIn("t2", refresh: "svpr1_B")));
                if (r.Headers["Authorization"] == "Bearer t1") return Http.Done(Http.Error(401, "token_expired", "Token expired"));
                return Http.Done(Http.Error(409, "version_conflict"));
            };
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Data.WriteAsync(IfVersionWrite()));
            Assert.AreEqual(2, kit.Transport.To("PATCH", "/player-data/me").Count);
            Assert.IsTrue(e.IsOutcomeUnknown);
            Assert.AreEqual(SvApiException.OutcomeUnknownCode, e.ErrorCode);
        }

        [TestMethod]
        public async Task FirstSendConflict_IsAPlainConflict()
        {
            var kit = await SignedInAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(409, "version_conflict"));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Data.WriteAsync(IfVersionWrite()));
            Assert.IsFalse(e.IsOutcomeUnknown);
            Assert.IsTrue(e.Is("version_conflict"));
        }

        [TestMethod]
        public async Task WriteWithoutIfVersion_NoAnswer_IsFlagged_ReadsNever()
        {
            var kit = await SignedInAsync();
            kit.Transport.Handler = r => Http.NetworkFailure();
            var write = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Data.WriteAsync(new SvDataOps().Inc("wins", 1)));
            Assert.IsTrue(write.IsOutcomeUnknown);
            var read = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Data.GetMineAsync());
            Assert.IsFalse(read.IsOutcomeUnknown, "a read changes nothing");
            kit.Transport.Handler = r => Http.Done(Http.Error(400, "key_invalid"));
            var refused = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Data.WriteAsync(new SvDataOps().Inc("wins", 1)));
            Assert.IsFalse(refused.IsOutcomeUnknown, "a refusal changed nothing");
        }
    }

    /// <summary>Rule 3: store changes carry their Idempotency-Key on the error.</summary>
    [TestClass]
    public class IdempotencyKeyOnErrorTests
    {
        [TestMethod]
        public async Task NoAnswer_TheErrorCarriesTheKey_AndSendingWithItFinishesTheSameChange()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.NetworkFailure();
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500));
            Assert.IsTrue(e.IsOutcomeUnknown);
            Assert.IsNotNull(e.IdempotencyKey);
            var sent = kit.Transport.To("POST", "/store/me/purchases");
            Assert.IsTrue(sent.All(r => r.Headers["Idempotency-Key"] == e.IdempotencyKey));

            // The "Retry" button: the same change again WITH the key (never a new purchase).
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.Txn(true)));
            var result = await kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500, idempotencyKey: e.IdempotencyKey);
            Assert.IsTrue(result.Replayed);
            Assert.AreEqual(e.IdempotencyKey, kit.Transport.Requests.Last().Headers["Idempotency-Key"]);
            Assert.AreEqual(sent[0].Body, kit.Transport.Requests.Last().Body, "byte-identical body");
            Assert.AreEqual(0, (await kit.Sv.Store.GetPendingAsync()).Count);
        }

        [TestMethod]
        public async Task Refusal_CarriesTheKeyToo_AndIsNotOutcomeUnknown()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(409, "insufficient_funds"));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.ConsumeSkuAsync("arrow", 2, idempotencyKey: "use-7"));
            Assert.AreEqual("use-7", e.IdempotencyKey);
            Assert.IsFalse(e.IsOutcomeUnknown);
        }

        [TestMethod]
        public async Task IdempotencyConflict_OnARepeat_StaysPendingAsUnknown_OnTheFirstSend_IsARefusal()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            var calls = 0;
            kit.Transport.Handler = r => ++calls == 1 ? Http.NetworkFailure() : Http.Done(Http.Error(409, "idempotency_conflict"));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500, idempotencyKey: "k-1"));
            Assert.IsTrue(e.IsOutcomeUnknown, "a repeat may race the earlier send");
            Assert.AreEqual("k-1", (await kit.Sv.Store.GetPendingAsync()).Single().IdempotencyKey);

            var fresh = new Kit();
            await fresh.SignInBySteamAsync();
            fresh.Transport.Handler = r => Http.Done(Http.Error(409, "idempotency_conflict"));
            var e2 = await Assert.ThrowsExceptionAsync<SvApiException>(() => fresh.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500, idempotencyKey: "k-2"));
            Assert.IsFalse(e2.IsOutcomeUnknown);
            Assert.AreEqual(0, (await fresh.Sv.Store.GetPendingAsync()).Count);
        }
    }

    /// <summary>Rule 12: one send per (player, Idempotency-Key) at a time.</summary>
    [TestClass]
    public class OneSendPerKeyTests
    {
        private static async Task<(Kit kit, TaskCompletionSource<SvHttpResponse> gate)> PendingThenBlockedResumeAsync()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync("tA");
            kit.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500, idempotencyKey: "k-1"));
            var gate = new TaskCompletionSource<SvHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            kit.Transport.Handler = r => gate.Task;
            return (kit, gate);
        }

        [TestMethod]
        public async Task RetryWithTheKey_DuringAResume_JoinsThatSend_NeverASecond()
        {
            var (kit, gate) = await PendingThenBlockedResumeAsync();
            var before = kit.Transport.Requests.Count;
            var resume = kit.Sv.Store.ResumePendingAsync();
            await Task.Delay(20);
            Assert.AreEqual(before + 1, kit.Transport.Requests.Count, "the resume's send is in flight");
            // The game's "Retry" passes the same key (and the same arguments) meanwhile.
            var retry = kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500, idempotencyKey: "k-1");
            await Task.Delay(20);
            Assert.AreEqual(before + 1, kit.Transport.Requests.Count, "a second concurrent send of the same change");

            gate.SetResult(Http.Json(200, Http.Txn(true)));
            var joined = await retry;
            var finished = await resume;
            Assert.AreEqual("k-1", joined.IdempotencyKey);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual(before + 1, kit.Transport.Requests.Count);
            Assert.AreEqual(0, (await kit.Sv.Store.GetPendingAsync()).Count);
        }

        [TestMethod]
        public async Task TwoResumesAtOnce_SendTheChangeOnce_TheSecondSkipsIt()
        {
            var (kit, gate) = await PendingThenBlockedResumeAsync();
            var before = kit.Transport.Requests.Count;
            var first = kit.Sv.Store.ResumePendingAsync();
            await Task.Delay(20);
            var second = kit.Sv.Store.ResumePendingAsync(); // e.g. after sign-in and on opening the store
            await Task.Delay(20);
            Assert.AreEqual(before + 1, kit.Transport.Requests.Count, "the same change was sent twice at once");
            // Rule 15: a resume never joins a send in flight; it skips it (that send decides) and returns at once.
            Assert.IsTrue(second.IsCompleted, "the second resume waited for the first one's send");
            Assert.AreEqual(0, (await second).Count);
            gate.SetResult(Http.Json(200, Http.Txn(true)));
            Assert.AreEqual(1, (await first).Count);
            Assert.AreEqual(before + 1, kit.Transport.Requests.Count);
        }

        [TestMethod]
        public async Task SameKey_AnotherRequest_WhileInFlight_FailsLocally_AndKeepsTheEntry()
        {
            var (kit, gate) = await PendingThenBlockedResumeAsync();
            var keptBody = (await kit.Sv.Store.GetPendingAsync()).Single().Body;
            var before = kit.Transport.Requests.Count;
            var resume = kit.Sv.Store.ResumePendingAsync();
            await Task.Delay(20);
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("shield", "GOLD", 300, idempotencyKey: "k-1"));
            Assert.AreEqual(SvApiException.ChangeInProgressCode, e.ErrorCode);
            // Rule 16: refused without being sent, so its own outcome is known (not applied) and it is not kept.
            Assert.IsFalse(e.IsOutcomeUnknown);
            Assert.IsFalse(e.IsPending);
            Assert.AreEqual("k-1", e.IdempotencyKey);
            Assert.AreEqual(before + 1, kit.Transport.Requests.Count, "nothing sent for the second request");
            Assert.AreEqual(keptBody, (await kit.Sv.Store.GetPendingAsync()).Single().Body, "the kept change was replaced");

            gate.SetResult(Http.Done(Http.Json(200, Http.Txn(true))).Result);
            Assert.AreEqual(1, (await resume).Count);
        }
    }

    /// <summary>Rule 13: a store that exists but cannot be opened is unreadable, never "nothing stored".</summary>
    [TestClass]
    public class UnreadableStoreTests
    {
        [TestMethod]
        public async Task UnreadableStore_RefusesANewGuest_SendsNothing_PlatformSignInStillWorks()
        {
            var kit = new Kit(store: new SvUnreadableSecureStore("locked"));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Auth.SignInAsGuestAsync());
            Assert.AreEqual(SvApiException.StorageUnreadableCode, e.ErrorCode);
            Assert.AreEqual(0, kit.Transport.Requests.Count, "a new guest would have been made over the stored one");
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Auth.RestoreAsync());

            await kit.SignInBySteamAsync();
            Assert.IsTrue(kit.Sv.Auth.IsSignedIn);
        }

        [TestMethod]
        public async Task FactoryThatFailsToOpen_GivesAnUnreadableStore_NotAnEmptyOne()
        {
            var before = SvPlayersDefaults.SecureStoreFactory;
            try
            {
                SvPlayersDefaults.SecureStoreFactory = name => throw new System.IO.IOException("keystore locked");
                var transport = new FakeTransport();
                var logs = new List<string>();
                var sv = new SvPlayers(new SvPlayersOptions
                {
                    GameId = 1, BaseUrl = FakeTransport.Base, Transport = transport, Logger = (l, m) => logs.Add(l + " " + m)
                });
                var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => sv.Auth.HasGuestOnDeviceAsync());
                Assert.AreEqual(SvApiException.StorageUnreadableCode, e.ErrorCode);
                Assert.AreEqual(0, transport.Requests.Count);
                Assert.IsTrue(logs.Any(l => l.Contains("could not be opened")), string.Join("\n", logs));
            }
            finally
            {
                SvPlayersDefaults.SecureStoreFactory = before;
            }
        }
    }

    /// <summary>Rule 5: no redirects, no plain HTTP except loopback (or the explicit development opt-in).</summary>
    [TestClass]
    public class RedirectAndHttpTests
    {
        [DataTestMethod]
        [DataRow(301)]
        [DataRow(302)]
        [DataRow(307)]
        [DataRow(308)]
        public async Task Redirect_IsANonRepeatedError(int status)
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(new SvHttpResponse(status, "<html>moved</html>",
                new Dictionary<string, string> { ["Location"] = "https://elsewhere.example/rest/store/me" }));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.GetMineAsync());
            Assert.AreEqual(status, e.Status);
            Assert.IsFalse(e.IsTransient);
            StringAssert.Contains(e.Message, "redirect");
            Assert.AreEqual(1, kit.Transport.Requests.Count(r => FakeTransport.PathOf(r) == "/store/me"));
            Assert.IsFalse(kit.Transport.Requests.Any(r => r.Url.Contains("elsewhere")));
        }

        [DataTestMethod]
        [DataRow("http://127.0.0.1:8787/rest")]
        [DataRow("http://localhost:8787/rest")]
        [DataRow("http://[::1]:8787/rest")]
        [DataRow("https://splattervault.com/rest")]
        public void LoopbackHttpAndHttps_AreAccepted(string url)
        {
            var sv = new SvPlayers(new SvPlayersOptions { GameId = 1, BaseUrl = url, Transport = new FakeTransport(), SecureStore = new SvMemorySecureStore() });
            Assert.AreEqual(url, sv.BaseUrl);
        }

        [DataTestMethod]
        [DataRow("http://splattervault.com/rest")]
        [DataRow("http://192.168.1.20:8787/rest")]
        [DataRow("http://localhost.example.com/rest")]
        [DataRow("ftp://splattervault.com/rest")]
        public void PlainHttpElsewhere_IsRefused_UnlessTheDevelopmentOptIn(string url)
        {
            Assert.ThrowsException<ArgumentException>(() =>
                new SvPlayers(new SvPlayersOptions { GameId = 1, BaseUrl = url, Transport = new FakeTransport(), SecureStore = new SvMemorySecureStore() }));
            if (!url.StartsWith("http://")) return;
            var dev = new SvPlayers(new SvPlayersOptions
            {
                GameId = 1, BaseUrl = url, AllowInsecureHttp = true, Transport = new FakeTransport(), SecureStore = new SvMemorySecureStore()
            });
            Assert.AreEqual(url, dev.BaseUrl);
        }
    }

    /// <summary>Rule 6: numeric options out of range are refused at configuration time.</summary>
    [TestClass]
    public class OptionValidationTests
    {
        private static SvPlayersOptions Options(Action<SvPlayersOptions> tweak)
        {
            var o = new SvPlayersOptions { GameId = 1, BaseUrl = FakeTransport.Base, Transport = new FakeTransport(), SecureStore = new SvMemorySecureStore() };
            tweak(o);
            return o;
        }

        [TestMethod]
        public void Timeouts_AndRenewLead_OutOfRange_AreRefused()
        {
            var bad = new Action<SvPlayersOptions>[]
            {
                o => o.RequestTimeout = TimeSpan.Zero,
                o => o.RequestTimeout = TimeSpan.FromSeconds(-1),
                o => o.RequestTimeout = TimeSpan.FromMilliseconds(int.MaxValue + 1L),
                o => o.RequestTimeout = TimeSpan.MaxValue,
                o => o.RefreshTimeout = TimeSpan.Zero,
                o => o.RefreshTimeout = TimeSpan.FromSeconds(10.001),
                o => o.RenewBeforeExpiry = TimeSpan.FromSeconds(-1),
                o => o.RenewBeforeExpiry = TimeSpan.FromMinutes(31)
            };
            foreach (var tweak in bad)
                Assert.ThrowsException<ArgumentOutOfRangeException>(() => new SvPlayers(Options(tweak)));

            var good = new SvPlayers(Options(o =>
            {
                o.RequestTimeout = TimeSpan.FromMilliseconds(int.MaxValue);
                o.RefreshTimeout = TimeSpan.FromSeconds(10);
                o.RenewBeforeExpiry = TimeSpan.Zero;
            }));
            Assert.IsNotNull(good);
        }

        [TestMethod]
        public void RetryPolicy_OutOfRange_IsRefusedBySetters()
        {
            var p = new SvRetryPolicy();
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => p.MaxAttempts = 0);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => p.MaxAttempts = SvRetryPolicy.MaxAttemptsLimit + 1);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => p.Jitter = double.NaN);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => p.Jitter = double.PositiveInfinity);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => p.Jitter = -0.1);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => p.Jitter = 1.1);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => p.BaseDelay = TimeSpan.FromMilliseconds(-1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => p.MaxBackoff = TimeSpan.MaxValue);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => p.MaxRetryAfter = TimeSpan.FromDays(30));
            // Unchanged by the refused values; the limits themselves are accepted.
            Assert.AreEqual(3, p.MaxAttempts);
            Assert.AreEqual(1.0, p.Jitter);
            p.MaxAttempts = 1;
            p.MaxAttempts = SvRetryPolicy.MaxAttemptsLimit;
            p.Jitter = 0;
            p.MaxBackoff = SvRetryPolicy.MaxWait;
            Assert.AreEqual(SvRetryPolicy.MaxWait, p.MaxBackoff);
        }
    }
}
