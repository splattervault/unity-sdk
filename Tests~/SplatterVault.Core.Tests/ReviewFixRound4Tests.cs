using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SplatterVault.Players;

namespace SplatterVault.Core.Tests
{
    /// <summary>The independent review of the 4.0 RC (2026-11-15): the LOW items and rule 16/17 details.</summary>
    [TestClass]
    public class ReviewRound4Tests
    {
        private const string PlayerB = "bbbbbbbb-0000-4000-8000-00000000000b";

        // ── IsPending (rule 16) ──

        [DataTestMethod]
        [DataRow(0, true)]     // no answer
        [DataRow(408, true)]
        [DataRow(429, true)]
        [DataRow(503, true)]
        [DataRow(500, true)]
        [DataRow(401, true)]   // a token problem: the change was not looked at
        [DataRow(409, false)]  // a refusal: nothing changed, not kept
        public async Task StoreErrors_SayWhetherTheChangeIsStillKept(int status, bool pending)
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => status == 0
                ? Http.NetworkFailure()
                : Http.Done(Http.Error(status, status == 409 ? "insufficient_funds" : status == 401 ? "token_invalid" : "busy", "x",
                    ("Retry-After", "60")));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword", "GOLD", 5, idempotencyKey: "k"));
            Assert.AreEqual(pending, e.IsPending, "status " + status);
            Assert.AreEqual("k", e.IdempotencyKey);
            if (status != 401) Assert.AreEqual(pending ? 1 : 0, (await kit.Sv.Store.GetPendingAsync()).Count);
        }

        [TestMethod]
        public async Task ChangeThatCouldNotBeKept_IsNotPending()
        {
            var flaky = new FlakyStore(new SvMemorySecureStore());
            var kit = new Kit(store: flaky);
            await kit.SignInBySteamAsync();
            flaky.FailWrites.Add("pending_store_changes");
            kit.Transport.Handler = r => Http.NetworkFailure();
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword", "GOLD", 5));
            Assert.IsFalse(e.IsPending, "storage failed: nothing kept to finish");
            Assert.IsTrue(e.IsOutcomeUnknown);
        }

        // ── same key, other arguments, kept (not in flight): refused, kept change untouched (rule 16) ──

        [TestMethod]
        public async Task SameKey_OtherArguments_WhileKept_IsRefused_AndTheKeptChangeIsUntouched()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword", "GOLD", 5, idempotencyKey: "k-7"));
            var keptBody = (await kit.Sv.Store.GetPendingAsync()).Single().Body;
            var sends = kit.Transport.Requests.Count;

            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("shield", "GOLD", 9, idempotencyKey: "k-7"));
            Assert.AreEqual(SvApiException.ChangeInProgressCode, e.ErrorCode);
            Assert.IsFalse(e.IsPending);
            Assert.AreEqual(sends, kit.Transport.Requests.Count, "the other request was sent");
            Assert.AreEqual(keptBody, (await kit.Sv.Store.GetPendingAsync()).Single().Body, "the kept change was replaced");

            // The same request with its key (a "Retry") sends the kept change as it was kept.
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.Txn(true)));
            var r2 = await kit.Sv.Store.PurchaseAsync("sword", "GOLD", 5, idempotencyKey: "k-7");
            Assert.AreEqual("k-7", r2.IdempotencyKey);
            Assert.AreEqual(keptBody, kit.Transport.Requests.Last().Body);
            Assert.AreEqual(0, (await kit.Sv.Store.GetPendingAsync()).Count);
        }

        // ── shared sends are not cancellable by one waiter; cancellation carries the key (rule 17) ──

        [TestMethod]
        public async Task CancellingAWait_LeavesTheSendRunning_AndSaysWhichChangeItWas()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            var gate = new TaskCompletionSource<SvHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            kit.Transport.Handler = r => gate.Task;
            using (var cts = new CancellationTokenSource())
            {
                var buy = kit.Sv.Store.PurchaseAsync("sword", "GOLD", 5, idempotencyKey: "k-c", ct: cts.Token);
                await Task.Delay(20);
                cts.Cancel();
                var e = await Assert.ThrowsExceptionAsync<SvChangeCanceledException>(() => buy);
                Assert.AreEqual("k-c", e.IdempotencyKey);
                Assert.IsTrue(e.IsPending);
                Assert.IsInstanceOfType(e, typeof(OperationCanceledException));
            }
            // The send was not cancelled: its answer still finishes (and drops) the kept change.
            Assert.AreEqual(1, (await kit.Sv.Store.GetPendingAsync()).Count);
            gate.SetResult(Http.Json(201, Http.Txn(false)));
            for (var i = 0; i < 50 && (await kit.Sv.Store.GetPendingAsync()).Count > 0; i++) await Task.Delay(10);
            Assert.AreEqual(0, (await kit.Sv.Store.GetPendingAsync()).Count, "the send was cancelled with the first caller's token");
            Assert.AreEqual(1, kit.Transport.To("POST", "/store/me/purchases").Count);
        }

        /// <summary>A memory store that runs a hook once, after the delete of the item with this suffix.</summary>
        private sealed class DeleteHookStore : ISecureStore
        {
            private readonly SvMemorySecureStore _inner = new SvMemorySecureStore();
            public string OnDelete;
            public Func<Task> Hook;
            public Task<string> GetAsync(string key) => _inner.GetAsync(key);
            public Task SetAsync(string key, string value) => _inner.SetAsync(key, value);

            public async Task DeleteAsync(string key)
            {
                await _inner.DeleteAsync(key);
                if (OnDelete == null || !key.EndsWith(OnDelete)) return;
                var hook = Hook;
                Hook = null;
                if (hook != null) await hook();
            }
        }

        [TestMethod]
        public async Task StudioTokenSignIn_StartsItsEpochBeforeSaving_ALateRestoreCannotWin()
        {
            var store = new DeleteHookStore();
            var first = new Kit(store: store);
            await first.SignInByEmailAsync("t1", "svpr1_A");
            var kit = new Kit(store: store); // next launch
            var gate = new TaskCompletionSource<SvHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var studioToken = Http.JwtWith(new JObject { ["sub"] = PlayerB, ["amr"] = new JArray("studio"), ["iat"] = 1794744000, ["exp"] = 1794747600 });
            kit.Transport.Handler = r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/sign-out") return Http.Done(Http.Json(200, new JObject()));
                if (path == "/player-auth/refresh") return gate.Task;
                if (path == "/player-auth/me")
                    return Http.Done(Http.Json(200, new JObject
                    {
                        ["player"] = new JObject { ["id"] = PlayerB, ["displayName"] = null, ["createdAt"] = "2026-11-01T00:00:00.000Z" },
                        ["game"] = new JObject { ["gameKey"] = "g" },
                        ["identities"] = new JArray()
                    }));
                return Http.Done(Http.Error(404, "not_found"));
            };
            var restore = kit.Sv.Auth.RestoreAsync();
            // The restore's refresh answer lands while the studio sign-in drops the stored email token.
            store.OnDelete = "refresh_token";
            store.Hook = async () =>
            {
                gate.SetResult(Http.Json(200, Http.SignIn("tA2", refresh: "svpr1_A2")));
                try
                {
                    await restore;
                }
                catch (Exception)
                {
                }
            };
            await kit.Sv.Auth.SignInWithStudioTokenAsync(studioToken);
            Assert.AreEqual(PlayerB, kit.Sv.Auth.PlayerId);
            Assert.AreEqual(studioToken, kit.Sv.Auth.PlayerToken);
            Assert.IsFalse(restore.Result, "the late restore was accepted");
            Assert.IsNull(await store.GetAsync(kit.Sv.StoragePrefix + "refresh_token"), "the late restore's token was kept");
            CollectionAssert.Contains(kit.Transport.To("POST", "/player-auth/sign-out").Select(r => (string)Kit.Body(r)["refreshToken"]).ToList(), "svpr1_A2");
        }

        // ── password change (rule 14) ──

        [TestMethod]
        public async Task PasswordChange_AnsweredAfterAnotherSignIn_KeepsNothing_AndEndsTheNewToken()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync("t1", "svpr1_A");
            kit.Transport.Handler = async r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/sign-out") return Http.Json(200, new JObject());
                if (path == "/player-auth/email/password") return Http.Json(200, Http.SignIn("tB", refresh: "svpr1_B", playerId: PlayerB));
                if (path == "/player-auth/me/password")
                {
                    await kit.Sv.Auth.SignInWithPasswordAsync("b@example.com", "pw"); // B signs in meanwhile
                    return Http.Json(200, new JObject { ["refreshToken"] = "svpr1_A2", ["refreshExpiresAt"] = "2026-12-14T12:00:00.000Z" });
                }
                return Http.Json(404, new JObject());
            };
            await kit.Sv.Auth.ChangePasswordAsync("old password", "new password");
            Assert.AreEqual(PlayerB, kit.Sv.Auth.PlayerId);
            Assert.AreEqual("svpr1_B", kit.Recording.RefreshToken(), "A's new refresh token replaced B's");
            Assert.AreEqual(PlayerB, kit.Recording.RefreshOwner());
            CollectionAssert.Contains(kit.Transport.To("POST", "/player-auth/sign-out").Select(r => (string)Kit.Body(r)["refreshToken"]).ToList(), "svpr1_A2");
        }

        [TestMethod]
        public async Task PasswordChange_SamePlayer_SavesTheNewTokenForThem()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync("t1", "svpr1_A");
            kit.Transport.Handler = r => Http.Done(Http.Json(200, new JObject { ["refreshToken"] = "svpr1_A2" }));
            await kit.Sv.Auth.ChangePasswordAsync("old password", "new password");
            Assert.AreEqual("svpr1_A2", kit.Recording.RefreshToken());
            Assert.AreEqual("0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b", kit.Recording.RefreshOwner());
        }

        // ── erase (rule 14) ──

        [TestMethod]
        public async Task Erase_WhileAnotherPlayerSignsIn_LeavesTheirRecordsAndSession()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync("t1", "svpr1_A");
            kit.Transport.Handler = async r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path == "/player-auth/sign-out") return Http.Json(200, new JObject());
                if (path == "/player-auth/email/password") return Http.Json(200, Http.SignIn("tB", refresh: "svpr1_B", playerId: PlayerB));
                if (path == "/player-auth/me" && r.Method == "DELETE")
                {
                    await kit.Sv.Auth.SignInWithPasswordAsync("b@example.com", "pw"); // B signs in during the DELETE
                    return Http.Json(204, null);
                }
                return Http.Json(404, new JObject());
            };
            await kit.Sv.Auth.EraseMeAsync();
            Assert.IsTrue(kit.Sv.Auth.IsSignedIn, "B's session was cleared by A's erase");
            Assert.AreEqual(PlayerB, kit.Sv.Auth.PlayerId);
            Assert.AreEqual("svpr1_B", kit.Recording.RefreshToken(), "B's refresh token was deleted");
            Assert.AreEqual("email", await kit.Sv.Auth.GetLastSignInMethodAsync());
        }

        [TestMethod]
        public async Task Erase_Normally_ForgetsTheirRecords_AndSignsOut()
        {
            var kit = new Kit();
            await kit.SignInByEmailAsync("t1", "svpr1_A");
            kit.Transport.Handler = r => Http.Done(Http.Json(204, null));
            await kit.Sv.Auth.EraseMeAsync();
            Assert.IsFalse(kit.Sv.Auth.IsSignedIn);
            Assert.IsNull(kit.Recording.RefreshToken());
            Assert.IsNull(await kit.Sv.Auth.GetLastSignInMethodAsync());
        }
    }

    /// <summary>Rule 17: temp files have unique names per write; recovery still finds leftovers.</summary>
    [TestClass]
    public class UniqueTempFileTests
    {
        private string _dir;

        [TestInitialize]
        public void Init() => _dir = Path.Combine(Path.GetTempPath(), "sv-tmp-test-" + Guid.NewGuid().ToString("N"));

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private SvProtectedFileStore Make()
        {
            var salt = SvAesBlobProtector.LoadOrCreateSalt(Path.Combine(_dir, "store.salt"));
            return new SvProtectedFileStore(_dir, new SvAesBlobProtector("m", salt));
        }

        private string FileOf(string key) =>
            Path.Combine(_dir, SvHash.Hex(SvHash.Sha256(System.Text.Encoding.UTF8.GetBytes(key))).Substring(0, 32) + ".svs");

        [TestMethod]
        public async Task EachWrite_UsesItsOwnTempName()
        {
            var store = Make();
            var names = new HashSet<string>();
            using (var watcher = new FileSystemWatcher(_dir, "*.tmp"))
            {
                watcher.Created += (s, e) => { lock (names) names.Add(e.Name); };
                watcher.EnableRaisingEvents = true;
                for (var i = 0; i < 5; i++) await store.SetAsync("k", "v" + i);
                await Task.Delay(200);
            }
            Assert.AreEqual("v4", await store.GetAsync("k"));
            Assert.AreEqual(0, Directory.GetFiles(_dir, "*.tmp").Length);
            lock (names)
            {
                Assert.IsTrue(names.Count >= 2, "temp names seen: " + string.Join(",", names));
                Assert.IsFalse(names.Contains(Path.GetFileName(FileOf("k")) + ".tmp"), "the shared temp name is still used");
            }
        }

        [TestMethod]
        public async Task SeveralLeftovers_TheNewestCompleteOneWins_AndAllAreRemoved()
        {
            var store = Make();
            await store.SetAsync("k", "older");
            var older = File.ReadAllBytes(FileOf("k"));
            await store.SetAsync("k", "newer");
            var newer = File.ReadAllBytes(FileOf("k"));
            File.Delete(FileOf("k"));
            var a = FileOf("k") + ".aaaa.tmp";
            var b = FileOf("k") + ".bbbb.tmp";
            var torn = FileOf("k") + ".cccc.tmp";
            File.WriteAllBytes(a, older);
            File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(-2));
            File.WriteAllBytes(b, newer);
            File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(-1));
            File.WriteAllBytes(torn, newer.Take(10).ToArray()); // the newest, but cut short
            Assert.AreEqual("newer", await Make().GetAsync("k"));
            Assert.AreEqual(0, Directory.GetFiles(_dir, "*.tmp").Length);
        }

        [TestMethod]
        public async Task Delete_RemovesEveryTempFileOfTheValue()
        {
            var store = Make();
            await store.SetAsync("k", "secret");
            File.Copy(FileOf("k"), FileOf("k") + ".1111.tmp");
            File.Copy(FileOf("k"), FileOf("k") + ".tmp");
            await store.DeleteAsync("k");
            Assert.AreEqual(0, Directory.GetFiles(_dir, "*.tmp").Length);
            Assert.IsNull(await store.GetAsync("k"));
        }
    }
}
