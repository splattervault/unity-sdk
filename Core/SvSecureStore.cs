using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SplatterVault.Core
{
    /// <summary>
    /// Where the SDK keeps a player's long-lived secrets on the device: the refresh token of an email sign-in
    /// (<c>svpr1_…</c>), a guest's device secret (<c>svpd1_…</c>, its only copy), the last way the player signed
    /// in, and the store changes still waiting for an answer. The player token is never stored: it lives in memory
    /// only.
    /// <para>The Unity package picks one per platform (Android Keystore, iOS Keychain, Windows DPAPI, else an
    /// encrypted file). Plug in your own through <c>SvPlayersOptions.SecureStore</c>.</para>
    /// <para><b>Contract.</b> <see cref="GetAsync"/> returns null ONLY when there is certainly no value. When the
    /// store cannot be read, or a value exists but cannot be opened (another device, a changed key, a damaged
    /// file, a locked keychain), it must THROW (preferably <see cref="SvSecureStoreException"/>): the SDK then never
    /// makes a new guest over the old one, and tries the read again next time. <see cref="SetAsync"/> must keep the
    /// value once it has completed and throw when it cannot; <see cref="DeleteAsync"/> throws when it cannot
    /// delete (no error when there is nothing to delete).</para>
    /// </summary>
    public interface ISecureStore
    {
        /// <summary>The value; null when there is certainly none; throws when it cannot be read.</summary>
        Task<string> GetAsync(string key);

        Task SetAsync(string key, string value);

        /// <summary>Removes the value; no error when there is none.</summary>
        Task DeleteAsync(string key);
    }

    /// <summary>A secure store could not read, write or delete a value. "Not found" is never this: it is null.</summary>
    public sealed class SvSecureStoreException : Exception
    {
        public SvSecureStoreException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>Keeps values in memory only: nothing survives a restart (tests, or a game that wants no storage).</summary>
    public sealed class SvMemorySecureStore : ISecureStore
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public Task<string> GetAsync(string key)
        {
            lock (_values) return Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        }

        public Task SetAsync(string key, string value)
        {
            lock (_values) _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key)
        {
            lock (_values) _values.Remove(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>Seals and opens a stored value (a platform's data protection, or <see cref="SvAesBlobProtector"/>).</summary>
    public interface ISvBlobProtector
    {
        /// <summary>The sealed form of <paramref name="data"/>, bound to <paramref name="key"/>.</summary>
        byte[] Protect(byte[] data, string key);

        /// <summary>
        /// The value, or null when the blob does not open (tampered, another device, another key or salt). The file
        /// store turns null into an <see cref="SvSecureStoreException"/>: an unopenable value is never "missing".
        /// </summary>
        byte[] Unprotect(byte[] blob, string key);
    }

    /// <summary>
    /// One file per value in a directory, sealed by an <see cref="ISvBlobProtector"/>. File names are hashes of the
    /// keys; writes go to a temporary file first and then replace the old one. A value that exists but does not
    /// open throws <see cref="SvSecureStoreException"/>. <c>restrictPermissions</c> (the Unity package passes
    /// owner-only modes on macOS, Linux and Android) is called for the directory and every file it creates.
    /// <para>Sealing and opening can be slow (the Android Keystore: about 15 ms a call on a Quest 3, on the main thread),
    /// so each value this store wrote or opened is kept in memory with the sealed bytes it came from: a read still
    /// reads the file (a few bytes, cheap) and answers from memory only when they are those exact bytes; any other
    /// read (another process wrote, the file is gone or damaged) opens the file. Writes always go to the file
    /// (write-through). Only values that opened are kept: an unreadable value is never answered from memory, and
    /// never as missing; after any seal or open fails, nothing kept is used (the key itself may have changed). No
    /// file times are compared (on Android the first local-time conversion of a process loads the time zone data:
    /// about 2 s on the main thread).</para>
    /// </summary>
    public sealed class SvProtectedFileStore : ISecureStore
    {
        private readonly object _gate = new object();
        private readonly string _directory;
        private readonly ISvBlobProtector _protector;
        private readonly SvLogger _log;
        private readonly Action<string, bool> _restrict;
        private readonly Dictionary<string, Kept> _kept = new Dictionary<string, Kept>(StringComparer.Ordinal);
        private bool _sealOpensChecked;

        /// <summary>A value and the sealed bytes in its file it was opened from or sealed into.</summary>
        private sealed class Kept
        {
            public string Value;
            public byte[] Sealed;
        }

        /// <param name="directory">Where the files go (one directory per store).</param>
        /// <param name="protector">Seals and opens each value.</param>
        /// <param name="log">Optional log.</param>
        /// <param name="restrictPermissions">Called with (path, isDirectory) for each directory and file made; may be null.</param>
        public SvProtectedFileStore(string directory, ISvBlobProtector protector, SvLogger log = null,
            Action<string, bool> restrictPermissions = null)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("A directory is required.", nameof(directory));
            _directory = directory;
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
            _log = log;
            _restrict = restrictPermissions;
        }

        public string Directory => _directory;

        public Task<string> GetAsync(string key)
        {
            lock (_gate)
            {
                var path = PathFor(key);
                if (!File.Exists(path))
                {
                    _kept.Remove(key);
                    return Task.FromResult(RecoverTemp(path, key));
                }
                byte[] blob;
                try
                {
                    blob = File.ReadAllBytes(path);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    throw new SvSecureStoreException("A stored value could not be read (" + e.GetType().Name + ").", e);
                }
                // The very bytes a kept value came from: opening them again would give that value.
                if (_kept.TryGetValue(key, out var kept) && kept.Sealed.SequenceEqual(blob)) return Task.FromResult(kept.Value);
                _kept.Remove(key);
                var data = Open(blob, key);
                if (data == null)
                {
                    SvLog.Write(_log, SvLogLevel.Warning, "A stored value exists but could not be opened (file " + Path.GetFileName(path) + ").");
                    throw new SvSecureStoreException(
                        "A stored value exists but could not be opened (another device or account, a changed key or salt, or a damaged file).");
                }
                var text = Encoding.UTF8.GetString(data);
                Keep(key, text, blob);
                return Task.FromResult(text);
            }
        }

        public Task SetAsync(string key, string value)
        {
            if (value == null) return DeleteAsync(key);
            lock (_gate)
            {
                EnsureDirectory(_directory, _restrict);
                var plain = Encoding.UTF8.GetBytes(value);
                byte[] blob;
                try
                {
                    blob = _protector.Protect(plain, key);
                }
                catch (Exception)
                {
                    _kept.Clear();
                    throw;
                }
                if (!_sealOpensChecked)
                {
                    // Once per store: what it seals must open again (a key that encrypts but cannot decrypt would make
                    // every kept value unreadable, so nothing is written then). Later reads of this value need no opening.
                    var back = Open(blob, key);
                    if (back == null || !back.SequenceEqual(plain))
                        throw new SvSecureStoreException("A sealed value does not open again: this store cannot keep values.");
                    _sealOpensChecked = true;
                }
                var path = PathFor(key);
                _kept.Remove(key);
                WriteAtomic(path, blob, _restrict);
                // What is in the file is what was sealed (a write that did not land is never taken as stored), checked
                // without opening it; then the value is kept for later reads.
                if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(blob))
                    throw new SvSecureStoreException("A written value does not read back.");
                Keep(key, value, blob);
            }
            return Task.CompletedTask;
        }

        private void Keep(string key, string value, byte[] sealedBytes)
        {
            _kept[key] = new Kept { Value = value, Sealed = sealedBytes };
        }

        /// <summary>
        /// Opens <paramref name="blob"/>; when it does not open (null or a throw), every kept value is dropped: the key
        /// itself may have changed (for example an invalidated Keystore key), so later reads open their files again.
        /// </summary>
        private byte[] Open(byte[] blob, string key)
        {
            byte[] data;
            try
            {
                data = _protector.Unprotect(blob, key);
            }
            catch (Exception)
            {
                _kept.Clear();
                throw;
            }
            if (data == null) _kept.Clear();
            return data;
        }

        public Task DeleteAsync(string key)
        {
            lock (_gate)
            {
                var path = PathFor(key);
                _kept.Remove(key);
                // The temp files first: with only one left, a later read would bring the value back (RecoverTemp).
                foreach (var tmp in TempFilesOf(path)) File.Delete(tmp);
                if (File.Exists(path)) File.Delete(path);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// No value file, but a temp file from a write that stopped before its rename (the game quit or crashed): when it
        /// opens (sealed, so a torn file never does), that write was complete and it is put in place; otherwise it is
        /// deleted and nothing is stored. Never leaves a temp file with a secret behind.
        /// </summary>
        private string RecoverTemp(string path, string key)
        {
            // Newest first: of several interrupted writes, the last one that is complete wins.
            var temps = TempFilesOf(path).OrderByDescending(File.GetLastWriteTimeUtc).ToList();
            foreach (var tmp in temps)
            {
                byte[] data;
                try
                {
                    data = _protector.Unprotect(File.ReadAllBytes(tmp), key);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is SvSecureStoreException)
                {
                    throw new SvSecureStoreException("A half-written value could not be read (" + e.GetType().Name + ").", e);
                }
                if (data == null) continue;
                File.Move(tmp, path);
                _restrict?.Invoke(path, false);
                foreach (var other in temps)
                    if (other != tmp && File.Exists(other)) File.Delete(other);
                SvLog.Write(_log, SvLogLevel.Info, "A value whose write was interrupted was recovered (file " + Path.GetFileName(path) + ").");
                return Encoding.UTF8.GetString(data);
            }
            // None opens: those writes never completed. Nothing is stored, and no temp file with a secret is kept.
            foreach (var tmp in temps) File.Delete(tmp);
            return null;
        }

        /// <summary>
        /// The temp files of <paramref name="path"/>'s writes: <c>&lt;name&gt;.&lt;unique&gt;.tmp</c> (one per write, so two
        /// processes writing at once never share one), and <c>&lt;name&gt;.tmp</c> of earlier builds.
        /// </summary>
        internal static IEnumerable<string> TempFilesOf(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return Enumerable.Empty<string>();
            var name = Path.GetFileName(path);
            return System.IO.Directory.GetFiles(dir, name + ".*tmp")
                .Where(f => Path.GetFileName(f) == name + ".tmp" || Path.GetFileName(f).StartsWith(name + ".", StringComparison.Ordinal) &&
                            Path.GetFileName(f).EndsWith(".tmp", StringComparison.Ordinal))
                .ToList();
        }

        private string PathFor(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            return Path.Combine(_directory, SvHash.Hex(SvHash.Sha256(Encoding.UTF8.GetBytes(key))).Substring(0, 32) + ".svs");
        }

        /// <summary>Makes <paramref name="directory"/> when it does not exist, and restricts it.</summary>
        public static void EnsureDirectory(string directory, Action<string, bool> restrict = null)
        {
            if (!System.IO.Directory.Exists(directory)) System.IO.Directory.CreateDirectory(directory);
            restrict?.Invoke(directory, true);
        }

        /// <summary>
        /// Writes a temporary file (restricted before anything is written to it, flushed to disk), then puts it in place
        /// of <paramref name="path"/> with an atomic rename: <c>File.Replace</c> over an existing file, <c>File.Move</c> for
        /// a new one. Never deletes the old value before the new one is in place. Where the file system cannot replace
        /// (rare), the temp file is copied over the old one: a torn copy then reads as an error, never as missing.
        /// </summary>
        public static void WriteAtomic(string path, byte[] bytes, Action<string, bool> restrict = null)
        {
            // A temp file of its own: two writers (two processes sharing the store) never write into the same one.
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                restrict?.Invoke(tmp, false);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, null);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                }
                catch (IOException)
                {
                }
                File.Copy(tmp, path, true);
                File.Delete(tmp);
                restrict?.Invoke(path, false);
                return;
            }
            try
            {
                File.Move(tmp, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Made by someone else meanwhile (another process sharing the store): replace it, still atomically.
                File.Replace(tmp, path, null);
            }
        }
    }

    /// <summary>
    /// A secure store that exists but cannot be opened: every read and write throws <see cref="SvSecureStoreException"/>,
    /// so the SDK answers <c>storage_unreadable</c> (and never makes a new guest over the stored one) instead of
    /// treating it as empty. <see cref="Players.SvPlayers"/> uses it when the platform's store fails to open.
    /// </summary>
    public sealed class SvUnreadableSecureStore : ISecureStore
    {
        private readonly string _reason;
        private readonly Exception _cause;

        public SvUnreadableSecureStore(string reason, Exception cause = null)
        {
            _reason = string.IsNullOrEmpty(reason) ? "Secure storage could not be opened." : reason;
            _cause = cause;
        }

        public Task<string> GetAsync(string key) => Task.FromException<string>(new SvSecureStoreException(_reason, _cause));

        public Task SetAsync(string key, string value) => Task.FromException(new SvSecureStoreException(_reason, _cause));

        public Task DeleteAsync(string key) => Task.FromException(new SvSecureStoreException(_reason, _cause));
    }

    /// <summary>
    /// AES-256-CBC with HMAC-SHA256 (encrypt, then MAC), keys derived from <c>keyMaterial</c> and a random salt kept
    /// next to the values. The fallback where a platform has no secure storage: it keeps values from being read as
    /// plain text, but anyone who can read the app's files and knows the key material can open them.
    /// </summary>
    public sealed class SvAesBlobProtector : ISvBlobProtector
    {
        private const byte Version = 1;
        private const int IvBytes = 16;
        private const int MacBytes = 32;
        private readonly byte[] _encKey;
        private readonly byte[] _macKey;

        /// <param name="keyMaterial">Something of this device and app (the Unity package uses the app id and the device id).</param>
        /// <param name="salt">At least 16 random bytes kept with the store (see <see cref="LoadOrCreateSalt"/>).</param>
        public SvAesBlobProtector(string keyMaterial, byte[] salt)
        {
            if (salt == null || salt.Length < 16) throw new ArgumentException("A salt of at least 16 bytes is required.", nameof(salt));
            var ikm = Encoding.UTF8.GetBytes(keyMaterial ?? "");
            _encKey = Derive(ikm, salt, "splattervault-store-enc");
            _macKey = Derive(ikm, salt, "splattervault-store-mac");
        }

        /// <summary>
        /// The salt in <paramref name="path"/>, made (32 random bytes) when the file does not exist. A missing or
        /// replaced salt leaves the values sealed with the old one unopenable: they then read as errors
        /// (<see cref="SvSecureStoreException"/>), never as missing.
        /// </summary>
        public static byte[] LoadOrCreateSalt(string path, Action<string, bool> restrict = null)
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length >= 16) return existing;
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) SvProtectedFileStore.EnsureDirectory(dir, restrict);
            var salt = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            SvProtectedFileStore.WriteAtomic(path, salt, restrict);
            return salt;
        }

        public byte[] Protect(byte[] data, string key)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encKey;
                aes.GenerateIV();
                byte[] cipher;
                using (var enc = aes.CreateEncryptor()) cipher = enc.TransformFinalBlock(data, 0, data.Length);
                var blob = new byte[1 + IvBytes + cipher.Length + MacBytes];
                blob[0] = Version;
                Buffer.BlockCopy(aes.IV, 0, blob, 1, IvBytes);
                Buffer.BlockCopy(cipher, 0, blob, 1 + IvBytes, cipher.Length);
                var mac = Mac(blob, 1 + IvBytes + cipher.Length, key);
                Buffer.BlockCopy(mac, 0, blob, blob.Length - MacBytes, MacBytes);
                return blob;
            }
        }

        public byte[] Unprotect(byte[] blob, string key)
        {
            if (blob == null || blob.Length < 1 + IvBytes + 16 + MacBytes || blob[0] != Version) return null;
            var signedLength = blob.Length - MacBytes;
            var expected = Mac(blob, signedLength, key);
            var diff = 0;
            for (var i = 0; i < MacBytes; i++) diff |= expected[i] ^ blob[signedLength + i];
            if (diff != 0) return null;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encKey;
                var iv = new byte[IvBytes];
                Buffer.BlockCopy(blob, 1, iv, 0, IvBytes);
                aes.IV = iv;
                try
                {
                    using (var dec = aes.CreateDecryptor())
                        return dec.TransformFinalBlock(blob, 1 + IvBytes, signedLength - 1 - IvBytes);
                }
                catch (CryptographicException)
                {
                    return null;
                }
            }
        }

        private byte[] Mac(byte[] blob, int length, string key)
        {
            using (var h = new HMACSHA256(_macKey))
            {
                var keyBytes = Encoding.UTF8.GetBytes(key ?? "");
                var input = new byte[length + keyBytes.Length];
                Buffer.BlockCopy(blob, 0, input, 0, length);
                Buffer.BlockCopy(keyBytes, 0, input, length, keyBytes.Length);
                return h.ComputeHash(input);
            }
        }

        private static byte[] Derive(byte[] ikm, byte[] salt, string label)
        {
            byte[] prk;
            using (var h = new HMACSHA256(salt)) prk = h.ComputeHash(ikm);
            using (var h = new HMACSHA256(prk)) return h.ComputeHash(Encoding.UTF8.GetBytes(label + "\u0001"));
        }
    }

    internal static class SvHash
    {
        internal static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(data);
        }

        internal static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
