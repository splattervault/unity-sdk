using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SplatterVault.Players;

namespace SplatterVault.Core.Tests
{
    /// <summary>
    /// How many sealed operations (the platform crypto: Android Keystore AES-GCM, DPAPI, AES fallback) a guest sign-in and
    /// a purchase cost through the real file store. Each costs ~13-16 ms on a Quest 3's main thread.
    /// </summary>
    [TestClass]
    public class SealedOpCountTests
    {
        private string _dir;

        [TestInitialize]
        public void Init() => _dir = Path.Combine(Path.GetTempPath(), "sv-sealed-" + Guid.NewGuid().ToString("N"));

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        /// <summary>Counts seals and opens, delegating to the AES protector.</summary>
        internal sealed class CountingProtector : ISvBlobProtector
        {
            private readonly ISvBlobProtector _inner;
            public int Seals;
            public int Opens;
            public CountingProtector(ISvBlobProtector inner) => _inner = inner;

            public byte[] Protect(byte[] data, string key)
            {
                Interlocked.Increment(ref Seals);
                return _inner.Protect(data, key);
            }

            public byte[] Unprotect(byte[] blob, string key)
            {
                Interlocked.Increment(ref Opens);
                return _inner.Unprotect(blob, key);
            }

            public void Reset() => Seals = Opens = 0;
        }

        internal sealed class TraceStore : ISecureStore
        {
            private readonly ISecureStore _inner;
            public readonly System.Collections.Generic.List<string> Ops = new System.Collections.Generic.List<string>();
            public TraceStore(ISecureStore inner) => _inner = inner;
            private static string S(string k) => k.Substring(k.LastIndexOf('.') + 1);
            public Task<string> GetAsync(string key) { Ops.Add("get " + S(key)); return _inner.GetAsync(key); }
            public Task SetAsync(string key, string value) { Ops.Add("set " + S(key)); return _inner.SetAsync(key, value); }
            public Task DeleteAsync(string key) { Ops.Add("del " + S(key)); return _inner.DeleteAsync(key); }
        }

        private (Kit kit, CountingProtector counter) Make()
        {
            var counter = new CountingProtector(new SvAesBlobProtector("m", SvAesBlobProtector.LoadOrCreateSalt(Path.Combine(_dir, "store.salt"))));
            var store = new SvProtectedFileStore(_dir, counter);
            return (new Kit(store: store), counter);
        }

        [TestMethod]
        public async Task GuestSignIn_AndPurchases_SealedOperationCounts()
        {
            var (kit, counter) = Make();
            // As the API answers: a new guest gets its secret; a sign-in with the secret does not get it again.
            kit.Transport.Handler = r => Http.Done(FakeTransport.PathOf(r) == "/player-auth/device/new"
                ? Http.Json(200, Http.SignIn("tG", device: "svpd1_G", provider: "device"))
                : FakeTransport.PathOf(r) == "/player-auth/device"
                    ? Http.Json(200, Http.SignIn("tG2", provider: "device"))
                    : Http.Json(201, Http.Txn(false)));
            await kit.Sv.Auth.SignInAsGuestAsync();
            Console.WriteLine("guest sign-in: seals=" + counter.Seals + " opens=" + counter.Opens);
            var signIn = (counter.Seals, counter.Opens);

            counter.Reset();
            await kit.Sv.Store.PurchaseAsync("sword", "GOLD", 5);
            Console.WriteLine("purchase #1: seals=" + counter.Seals + " opens=" + counter.Opens);
            var first = (counter.Seals, counter.Opens);

            counter.Reset();
            await kit.Sv.Store.PurchaseAsync("sword", "GOLD", 5);
            Console.WriteLine("purchase #2: seals=" + counter.Seals + " opens=" + counter.Opens);

            counter.Reset();
            await kit.Sv.Store.GetPendingAsync();
            await kit.Sv.Store.ResumePendingAsync();
            Console.WriteLine("GetPending + Resume (nothing pending): seals=" + counter.Seals + " opens=" + counter.Opens);

            counter.Reset();
            var traced = new TraceStore(new SvProtectedFileStore(_dir, counter));
            var relaunch = new Kit(store: traced);
            relaunch.Transport.Handler = kit.Transport.Handler;
            await relaunch.Sv.Auth.RestoreAsync();
            Console.WriteLine("restore (new process): seals=" + counter.Seals + " opens=" + counter.Opens + " ops=" + string.Join(",", traced.Ops));
            // A guest sign-in seals its secret and the last sign-in, and opens one seal once per store (the check that
            // what it seals opens again); its read-back of the secret is answered from memory.
            Assert.AreEqual((2, 1), signIn);
            // A purchase: only the unavoidable sealed write of the kept change; reading the list back to drop it opens nothing.
            Assert.AreEqual((1, 0), first);
            // A restore in a new process opens what it must (the last sign-in, the guest secret) and seals nothing: the
            // unchanged last sign-in is not written again.
            Assert.AreEqual(0, counter.Seals, "restore sealed " + counter.Seals);
            Assert.AreEqual(2, counter.Opens, "restore opened " + counter.Opens);
        }

        private string FileOf(string key) =>
            Path.Combine(_dir, SvHash.Hex(SvHash.Sha256(System.Text.Encoding.UTF8.GetBytes(key))).Substring(0, 32) + ".svs");

        private (SvProtectedFileStore store, CountingProtector counter) Store()
        {
            var counter = new CountingProtector(new SvAesBlobProtector("m", SvAesBlobProtector.LoadOrCreateSalt(Path.Combine(_dir, "store.salt"))));
            return (new SvProtectedFileStore(_dir, counter), counter);
        }

        [TestMethod]
        public async Task OwnWrites_AreReadFromMemory()
        {
            var (store, counter) = Store();
            await store.SetAsync("k", "value-1");
            counter.Reset();
            for (var i = 0; i < 5; i++) Assert.AreEqual("value-1", await store.GetAsync("k"));
            Assert.AreEqual(0, counter.Opens);
            await store.SetAsync("k", "value-2");
            Assert.AreEqual("value-2", await store.GetAsync("k"));
            Assert.AreEqual((1, 0), (counter.Seals, counter.Opens), "a write seals once; its value is not opened again");
        }

        [TestMethod]
        public async Task AValueChangedByAnotherProcess_IsReadFromItsFile()
        {
            var (mine, counter) = Store();
            var (other, _) = Store(); // another process sharing the store
            await mine.SetAsync("k", "mine");
            Assert.AreEqual("mine", await mine.GetAsync("k"));
            await other.SetAsync("k", "theirs, longer");
            Assert.AreEqual("theirs, longer", await mine.GetAsync("k"));
            // The same length and the same write time as the copy it keeps: only the bytes differ, still read from the file.
            var before = File.GetLastWriteTimeUtc(FileOf("k"));
            await other.SetAsync("k", "THEIRS, LONGER");
            File.SetLastWriteTimeUtc(FileOf("k"), before);
            counter.Reset();
            Assert.AreEqual("THEIRS, LONGER", await mine.GetAsync("k"));
            Assert.AreEqual(1, counter.Opens);
        }

        [TestMethod]
        public async Task AKeptValueDamagedLater_IsUnreadable_NeverAnsweredFromMemory()
        {
            var (store, _) = Store();
            await store.SetAsync("k", "secret");
            Assert.AreEqual("secret", await store.GetAsync("k"));
            var written = File.GetLastWriteTimeUtc(FileOf("k"));
            var bytes = File.ReadAllBytes(FileOf("k"));
            bytes[bytes.Length - 3] ^= 0x55;
            File.WriteAllBytes(FileOf("k"), bytes);
            File.SetLastWriteTimeUtc(FileOf("k"), written); // same size, same time: only the bytes tell
            await Assert.ThrowsExceptionAsync<SvSecureStoreException>(() => store.GetAsync("k"));
            await Assert.ThrowsExceptionAsync<SvSecureStoreException>(() => store.GetAsync("k"), "an unreadable value was kept");
        }

        [TestMethod]
        public async Task AKeptValueDeletedByAnotherProcess_IsMissing()
        {
            var (mine, _) = Store();
            var (other, _) = Store();
            await mine.SetAsync("k", "secret");
            Assert.AreEqual("secret", await mine.GetAsync("k"));
            await other.DeleteAsync("k");
            Assert.IsNull(await mine.GetAsync("k"));
        }

        /// <summary>Works until <see cref="Broken"/>: then nothing opens and nothing seals (a key invalidated or replaced).</summary>
        private sealed class BreakableProtector : ISvBlobProtector
        {
            private readonly ISvBlobProtector _inner;
            public bool Broken;
            public int Opens;
            public BreakableProtector(ISvBlobProtector inner) { _inner = inner; }
            public byte[] Protect(byte[] data, string key) => Broken ? throw new InvalidOperationException("key invalidated") : _inner.Protect(data, key);
            public byte[] Unprotect(byte[] blob, string key) { Opens++; return Broken ? null : _inner.Unprotect(blob, key); }
        }

        private BreakableProtector Breakable() =>
            new BreakableProtector(new SvAesBlobProtector("m", SvAesBlobProtector.LoadOrCreateSalt(Path.Combine(_dir, "store.salt"))));

        [TestMethod]
        public async Task AfterAValueFailsToOpen_NothingKeptIsAnsweredFromMemory()
        {
            var key = Breakable();
            var store = new SvProtectedFileStore(_dir, key);
            await store.SetAsync("a", "A");
            await store.SetAsync("b", "B");
            Assert.AreEqual("B", await store.GetAsync("b"));
            key.Broken = true; // the key changed under the running game
            var (other, _) = Store();
            await other.SetAsync("a", "A2"); // another process wrote "a": this store must open its file, which fails
            await Assert.ThrowsExceptionAsync<SvSecureStoreException>(() => store.GetAsync("a"));
            key.Opens = 0;
            await Assert.ThrowsExceptionAsync<SvSecureStoreException>(() => store.GetAsync("b"), "a kept value was answered after the key failed");
            Assert.AreEqual(1, key.Opens);
        }

        [TestMethod]
        public async Task AfterASealFails_NothingKeptIsAnsweredFromMemory()
        {
            var key = Breakable();
            var store = new SvProtectedFileStore(_dir, key);
            await store.SetAsync("a", "A");
            await store.SetAsync("b", "B");
            key.Broken = true;
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.SetAsync("a", "A2"));
            await Assert.ThrowsExceptionAsync<SvSecureStoreException>(() => store.GetAsync("b"), "a kept value was answered after the key failed");
            Assert.AreEqual("A", await new SvProtectedFileStore(_dir, Breakable()).GetAsync("a"), "the failed write changed the file");
        }

        /// <summary>Seals, but what it seals never opens (a broken platform key).</summary>
        private sealed class OneWayProtector : ISvBlobProtector
        {
            public byte[] Protect(byte[] data, string key) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
            public byte[] Unprotect(byte[] blob, string key) => null;
        }

        [TestMethod]
        public async Task AStoreWhoseSealsDoNotOpen_WritesNothing_AndSaysSo()
        {
            var store = new SvProtectedFileStore(_dir, new OneWayProtector());
            await Assert.ThrowsExceptionAsync<SvSecureStoreException>(() => store.SetAsync("k", "guest secret"));
            Assert.IsFalse(File.Exists(FileOf("k")), "a value that could never be read back was written");
            Assert.IsNull(await store.GetAsync("k"));
        }

        [TestMethod]
        public async Task TheGuestSecret_IsStillReportedUnsaved_WhenTheStoreCannotKeepIt()
        {
            var kit = new Kit(store: new SvProtectedFileStore(_dir, new OneWayProtector()));
            kit.Transport.Handler = r => Http.Done(Http.Json(200, Http.SignIn("tG", device: "svpd1_G", provider: "device")));
            var r1 = await kit.Sv.Auth.SignInAsGuestAsync();
            Assert.IsFalse(r1.SavedToDevice, "a guest secret that is not on the device was reported saved");
        }

        [TestMethod]
        public async Task AnUnchangedLastSignIn_IsNotWrittenAgain()
        {
            var (store, counter) = Store();
            var kit = new Kit(store: store);
            kit.Transport.Handler = r => Http.Done(FakeTransport.PathOf(r) == "/player-auth/device/new"
                ? Http.Json(200, Http.SignIn("tG", device: "svpd1_G", provider: "device"))
                : Http.Json(200, Http.SignIn("tG2", provider: "device")));
            await kit.Sv.Auth.SignInAsGuestAsync();
            counter.Reset();
            await kit.Sv.Auth.SignInWithDeviceAsync(); // the same guest again: nothing to write
            Assert.AreEqual(0, counter.Seals);
            Assert.AreEqual("device", await kit.Sv.Auth.GetLastSignInMethodAsync());
        }
    }
}
