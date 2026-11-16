using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SplatterVault.Players;

namespace SplatterVault.Core.Tests
{
    [TestClass]
    public class StoreTests
    {
        [TestMethod]
        public async Task Purchase_Body_Key_AndAnswer()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(Http.Json(201, Http.Txn(false)));
            var result = await kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500);
            var request = kit.Transport.To("POST", "/store/me/purchases").Single();
            Assert.AreEqual("{\"sku\":\"sword_gold\",\"currency\":\"GOLD\",\"quantity\":1,\"expectedPrice\":500}", request.Body);
            Assert.IsTrue(Regex.IsMatch(request.Headers["Idempotency-Key"], "^[0-9a-f]{32}$"));
            Assert.AreEqual(request.Headers["Idempotency-Key"], result.IdempotencyKey);
            Assert.IsFalse(result.Replayed);
            Assert.AreEqual(700L, result.Balances.Single().Amount);
            Assert.AreEqual(-500L, result.Changes[0].Delta);
            Assert.AreEqual("c2a10000-0000-4000-8000-000000000002", result.Changes[1].InstanceId);
        }

        [TestMethod]
        public async Task Retries_ReuseTheSameKey()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            var calls = 0;
            kit.Transport.Handler = r =>
            {
                calls++;
                if (calls == 1) return Http.NetworkFailure();
                if (calls == 2) return Http.Done(Http.Error(503, null, "busy"));
                return Http.Done(Http.Json(200, Http.Txn(true)));
            };
            var result = await kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500);
            var keys = kit.Transport.To("POST", "/store/me/purchases").Select(r => r.Headers["Idempotency-Key"]).Distinct().ToList();
            Assert.AreEqual(3, kit.Transport.To("POST", "/store/me/purchases").Count);
            Assert.AreEqual(1, keys.Count);
            Assert.IsTrue(result.Replayed, "a replay (HTTP 200) is a success");
            Assert.AreEqual(0, (await kit.Sv.Store.GetPendingAsync()).Count);
        }

        [TestMethod]
        public async Task PendingChange_IsKeptBeforeTheSend_AndDroppedAfterTheAnswer()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(Http.Json(201, Http.Txn(false)));
            await kit.Sv.Store.ConsumeSkuAsync("arrow", 5);
            var events = kit.Transport.Events;
            var kept = events.FindIndex(e => e.StartsWith("store set pending_store_changes="));
            var sent = events.FindIndex(e => e.StartsWith("send POST /store/me/consume"));
            var dropped = events.FindIndex(e => e == "store delete pending_store_changes");
            Assert.IsTrue(kept >= 0 && kept < sent && sent < dropped, string.Join("\n", events));
            Assert.AreEqual("{\"sku\":\"arrow\",\"quantity\":5}", kit.Transport.Requests.Last().Body);
        }

        [TestMethod]
        public async Task NoAnswer_StaysPending_AndResumeSendsTheSameKeyAndBody()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.NetworkFailure();
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() =>
                kit.Sv.Store.ConsumeInstanceAsync("c2a10000-0000-4000-8000-000000000002", 2, idempotencyKey: "use-potion-77"));
            Assert.IsTrue(e.IsNetworkError);
            var pending = await kit.Sv.Store.GetPendingAsync();
            Assert.AreEqual("use-potion-77", pending.Single().IdempotencyKey);
            Assert.AreEqual("consume", pending.Single().Kind);

            // Next launch: same storage, same player.
            var relaunch = new Kit(store: kit.Store);
            await relaunch.SignInBySteamAsync();
            relaunch.Transport.Handler = r => Http.Done(Http.Json(200, Http.Txn(true)));
            var finished = await relaunch.Sv.Store.ResumePendingAsync();
            Assert.AreEqual(1, finished.Count);
            Assert.IsTrue(finished[0].Replayed);
            var resent = relaunch.Transport.To("POST", "/store/me/consume").Single();
            Assert.AreEqual("use-potion-77", resent.Headers["Idempotency-Key"]);
            Assert.AreEqual(kit.Transport.Requests.Last().Body, resent.Body);
            Assert.AreEqual("{\"instanceId\":\"c2a10000-0000-4000-8000-000000000002\",\"quantity\":2}", resent.Body);
            Assert.AreEqual(0, (await relaunch.Sv.Store.GetPendingAsync()).Count);
        }

        [TestMethod]
        public async Task Refusal_IsDefinitive_AndNotKept()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(Http.Error(409, "insufficient_funds"));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500));
            Assert.AreEqual("insufficient_funds", e.ErrorCode);
            Assert.AreEqual(1, kit.Transport.Requests.Count(r => r.Method == "POST" && r.Url.EndsWith("/purchases")));
            Assert.AreEqual(0, (await kit.Sv.Store.GetPendingAsync()).Count);
        }

        [TestMethod]
        public async Task Resume_RefusedChange_IsReportedAndDropped()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500));
            kit.Transport.Handler = r => Http.Done(Http.Error(409, "price_changed"));
            string refused = null;
            var finished = await kit.Sv.Store.ResumePendingAsync((change, error) => refused = error.ErrorCode);
            Assert.AreEqual(0, finished.Count);
            Assert.AreEqual("price_changed", refused);
            Assert.AreEqual(0, (await kit.Sv.Store.GetPendingAsync()).Count);
        }

        [TestMethod]
        public async Task Pending_BelongsToItsPlayer()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.NetworkFailure();
            await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Store.PurchaseAsync("sword_gold", "GOLD", 500));
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("t9", provider: "steam", playerId: "11111111-2222-4333-8444-555555555555")));
            await kit.Sv.Auth.SignInWithSteamAsync("CD");
            Assert.AreEqual(0, (await kit.Sv.Store.GetPendingAsync()).Count);
        }

        [TestMethod]
        public async Task HistoryPaging_CatalogAndMetaSync()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r =>
            {
                var path = FakeTransport.PathOf(r);
                if (path.StartsWith("/store/me/history"))
                    return Http.Done(new SvHttpResponse(200,
                        "{\"txns\":[{\"id\":\"t-1\",\"kind\":\"grant\",\"createdAt\":\"2026-11-13T19:00:00.000Z\",\"reverses\":null," +
                        "\"changes\":[{\"currency\":\"GOLD\",\"delta\":250,\"after\":1200}],\"shortfall\":[]}],\"next\":\"5b0e0000-0000-4000-8000-000000000009\"}"));
                if (path == "/store/me/platform-purchases/meta")
                    return Http.Done(new SvHttpResponse(200, "{\"granted\":2,\"alreadyGranted\":1,\"unmapped\":0,\"consumePending\":0}"));
                return Http.Done(Http.Error(404, "x"));
            };
            var page = await kit.Sv.Store.GetHistoryAsync();
            Assert.AreEqual("5b0e0000-0000-4000-8000-000000000009", page.Next);
            Assert.AreEqual(1200L, page.Txns[0].Changes[0].After);
            await kit.Sv.Store.GetHistoryAsync(page.Next);
            Assert.AreEqual("/store/me/history?before=5b0e0000-0000-4000-8000-000000000009", FakeTransport.PathOf(kit.Transport.Requests.Last()));
            var sync = await kit.Sv.Store.SyncMetaPurchasesAsync();
            Assert.AreEqual(2, sync.Granted);
            Assert.AreEqual("{}", kit.Transport.Requests.Last().Body);
            Assert.IsFalse(kit.Transport.Requests.Last().Headers.ContainsKey("Idempotency-Key"));
            await kit.Sv.Store.SyncMetaPurchasesAsync("1234567890");
            Assert.AreEqual("{\"appId\":\"1234567890\"}", kit.Transport.Requests.Last().Body);
        }

        [TestMethod]
        public async Task Arguments_AreCheckedLocally()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => kit.Sv.Store.PurchaseAsync("s", "GOLD", 5, quantity: 101));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => kit.Sv.Store.PurchaseAsync("s", "GOLD", -1));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => kit.Sv.Store.PurchaseAsync("s", "GOLD", 5, idempotencyKey: "has space"));
            Assert.AreEqual(1, kit.Transport.Requests.Count, "only the sign-in");
        }
    }

    [TestClass]
    public class SecureStorageTests
    {
        private string _dir;

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sv-store-test-" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private SvProtectedFileStore Make(string material = "com.studio.game|device-1")
        {
            var salt = SvAesBlobProtector.LoadOrCreateSalt(Path.Combine(_dir, "store.salt"));
            return new SvProtectedFileStore(_dir, new SvAesBlobProtector(material, salt));
        }

        [TestMethod]
        public async Task RoundTrip_Replace_Delete()
        {
            var store = Make();
            Assert.IsNull(await store.GetAsync("sv4.x.refresh_token"));
            await store.SetAsync("sv4.x.refresh_token", "svpr1_A");
            await store.SetAsync("sv4.x.refresh_token", "svpr1_B");
            Assert.AreEqual("svpr1_B", await Make().GetAsync("sv4.x.refresh_token"), "a new instance reads it (same salt and material)");
            await store.DeleteAsync("sv4.x.refresh_token");
            Assert.IsNull(await store.GetAsync("sv4.x.refresh_token"));
            await store.DeleteAsync("sv4.x.refresh_token");
        }

        [TestMethod]
        public async Task Files_HoldNoPlainText_AndNoKeyNames()
        {
            var store = Make();
            await store.SetAsync("sv4.x.device_secret", "svpd1_SECRETSECRET");
            foreach (var file in Directory.GetFiles(_dir))
            {
                Assert.IsFalse(Path.GetFileName(file).Contains("device"), file);
                var text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file));
                Assert.IsFalse(text.Contains("svpd1_"), file);
            }
        }

        [TestMethod]
        public async Task TamperedOrForeign_ValuesAreErrors_NeverMissing()
        {
            var store = Make();
            await store.SetAsync("k", "value");
            await Assert.ThrowsExceptionAsync<SvSecureStoreException>(() => Make("another device").GetAsync("k"));
            var file = Directory.GetFiles(_dir, "*.svs").Single();
            var bytes = File.ReadAllBytes(file);
            bytes[20] ^= 0xFF;
            File.WriteAllBytes(file, bytes);
            await Assert.ThrowsExceptionAsync<SvSecureStoreException>(() => store.GetAsync("k"));
            Assert.IsNull(await store.GetAsync("never-written"), "only a missing value is null");
        }

        [TestMethod]
        public async Task ReplacedSalt_MakesValuesUnreadable_NotMissing()
        {
            var store = Make();
            await store.SetAsync("k", "value");
            File.Delete(Path.Combine(_dir, "store.salt"));
            var fresh = Make(); // a new salt is made
            await Assert.ThrowsExceptionAsync<SvSecureStoreException>(() => fresh.GetAsync("k"));
        }

        // ── rule 13: atomic writes, interrupted writes, temp files ──

        private string FileOf(string key) =>
            Path.Combine(_dir, SvHash.Hex(SvHash.Sha256(System.Text.Encoding.UTF8.GetBytes(key))).Substring(0, 32) + ".svs");

        [TestMethod]
        public async Task Writes_LeaveNoTempFile_AndReplaceAtomically()
        {
            var store = Make();
            await store.SetAsync("k", "one");
            await store.SetAsync("k", "two");
            Assert.AreEqual("two", await store.GetAsync("k"));
            Assert.AreEqual(0, Directory.GetFiles(_dir, "*.tmp").Length);
        }

        [TestMethod]
        public async Task InterruptedFirstWrite_IsRecovered_FromItsCompleteTempFile()
        {
            var store = Make();
            await store.SetAsync("k", "svpd1_NEWGUEST");
            // The game stopped after the temp file was written and flushed, before the rename.
            File.Move(FileOf("k"), FileOf("k") + ".tmp");
            Assert.AreEqual("svpd1_NEWGUEST", await Make().GetAsync("k"), "a complete write was lost");
            Assert.IsTrue(File.Exists(FileOf("k")));
            Assert.IsFalse(File.Exists(FileOf("k") + ".tmp"));
        }

        [TestMethod]
        public async Task TornTempFile_IsDeleted_AndReadsAsNothingStored()
        {
            var store = Make();
            await store.SetAsync("k", "value");
            var bytes = File.ReadAllBytes(FileOf("k"));
            File.Delete(FileOf("k"));
            File.WriteAllBytes(FileOf("k") + ".tmp", bytes.Take(bytes.Length / 2).ToArray()); // a write cut short
            Assert.IsNull(await store.GetAsync("k"), "that write never completed");
            Assert.IsFalse(File.Exists(FileOf("k") + ".tmp"), "a temp file with a secret was left behind");
        }

        [TestMethod]
        public async Task Delete_RemovesTheTempFileToo_SoNothingComesBack()
        {
            var store = Make();
            await store.SetAsync("k", "svpr1_SECRET");
            File.Copy(FileOf("k"), FileOf("k") + ".tmp"); // a leftover of an interrupted write
            await store.DeleteAsync("k");
            Assert.IsFalse(File.Exists(FileOf("k") + ".tmp"), "the temp file outlived the delete (sign-out, erase)");
            Assert.IsNull(await store.GetAsync("k"), "the deleted value came back");
        }

        [TestMethod]
        public void UnreadableSalt_Throws_NeverMakesANewOne()
        {
            if (OperatingSystem.IsWindows()) return; // chmod-based
            var salt = Path.Combine(_dir, "store.salt");
            var original = SvAesBlobProtector.LoadOrCreateSalt(salt);
            File.SetUnixFileMode(salt, UnixFileMode.None); // exists, cannot be read
            try
            {
                Assert.ThrowsException<UnauthorizedAccessException>(() => SvAesBlobProtector.LoadOrCreateSalt(salt));
            }
            finally
            {
                File.SetUnixFileMode(salt, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            CollectionAssert.AreEqual(original, File.ReadAllBytes(salt), "the existing salt was replaced");
        }

        [TestMethod]
        public void Protector_BindsTheValueToItsKey()
        {
            var p = new SvAesBlobProtector("m", new byte[32]);
            var blob = p.Protect(new byte[] { 1, 2, 3 }, "refresh");
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, p.Unprotect(blob, "refresh"));
            Assert.IsNull(p.Unprotect(blob, "device"), "a value copied under another key does not open");
        }
    }
}
