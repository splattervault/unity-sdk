#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SplatterVault.Core;
using UnityEngine;

namespace SplatterVault.Players.Unity
{
    /// <summary>
    /// Android (Meta Quest included) secure storage: an AES-256-GCM key made in and never leaving the Android
    /// Keystore (<c>AndroidKeyStore</c>, Android 6+) seals each value, bound to its key name (GCM additional data).
    /// The sealed values are owner-only files in the app's no-backup directory
    /// (<c>Context.getNoBackupFilesDir()/SplatterVault/keystore/&lt;name&gt;</c>), which Android Auto Backup, cloud
    /// backups and device-to-device transfers never copy: a value sealed with this device's Keystore key could not be
    /// opened anywhere else, so it is never moved there (no manifest or backup-rule change is needed in the game).
    /// No Java plugin or extra Android dependency: plain JNI through AndroidJavaObject; calls that take a null, an
    /// interface-typed object or a byte array use explicit JNI signatures (AndroidJNI), so overload lookup never
    /// guesses.
    /// <para>Every call runs on Unity's main thread (a call from another thread is handed to it) under one lock
    /// shared by all instances, so two <see cref="SvPlayers"/> never race to make the key. The key handle, the Cipher
    /// and the JNI lookups are made once per process, and the file store keeps each value it wrote or opened in memory
    /// (checked against its file before every read), so a read of a known value opens nothing and a write seals once.</para>
    /// <para>A sealed value that does not open (for example the app's files copied by hand to another device, whose
    /// Keystore key did not come along) throws <see cref="SvSecureStoreException"/>: the SDK reports
    /// <c>storage_unreadable</c> instead of treating the guest as gone.</para>
    /// </summary>
    public sealed class AndroidKeystoreSecureStore : ISecureStore
    {
        private const int PurposeEncrypt = 1;
        private const int PurposeDecrypt = 2;
        private const int EncryptMode = 1;
        private const int DecryptMode = 2;
        private const int GcmTagBits = 128;
        private const byte Format = 1;
        private const string Transformation = "AES/GCM/NoPadding";

        /// <summary>One lock for every instance: the Keystore key and the files are shared by name.</summary>
        private static readonly object Gate = new object();

        private readonly SvProtectedFileStore _files;

        public AndroidKeystoreSecureStore(string name, SvLogger log = null)
        {
            var directory = Path.Combine(NoBackupDirectory(), "SplatterVault", "keystore", name);
            _files = new SvProtectedFileStore(directory, new KeystoreProtector("splattervault." + name, log), log,
                (path, isDirectory) => SvUnixPermissions.Restrict(path, isDirectory));
        }

        /// <summary>Where the sealed values are kept (inside the app's no-backup directory).</summary>
        public string Directory => _files.Directory;

        /// <summary>The Keystore can hold AES keys (Android 6, API 23, and later).</summary>
        public static bool IsSupported
        {
            get
            {
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                    return version.GetStatic<int>("SDK_INT") >= 23;
            }
        }

        /// <summary>
        /// The app's no-backup files directory (<c>Context.getNoBackupFilesDir()</c>, Android 5+): private to the app and
        /// never copied by Auto Backup or a device transfer.
        /// </summary>
        public static string NoBackupDirectory()
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
            using (var dir = context.Call<AndroidJavaObject>("getNoBackupFilesDir"))
            {
                var path = dir.Call<string>("getAbsolutePath");
                if (string.IsNullOrEmpty(path)) throw new SvSecureStoreException("The app has no no-backup files directory.");
                return path;
            }
        }

        public Task<string> GetAsync(string key)
        {
            return SvUnityMainThread.RunAsync(() =>
            {
                lock (Gate) return _files.GetAsync(key).GetAwaiter().GetResult();
            });
        }

        public Task SetAsync(string key, string value)
        {
            if (value == null) return DeleteAsync(key);
            return SvUnityMainThread.RunAsync(() =>
            {
                lock (Gate) _files.SetAsync(key, value).GetAwaiter().GetResult();
                return true;
            });
        }

        public Task DeleteAsync(string key)
        {
            return SvUnityMainThread.RunAsync(() =>
            {
                lock (Gate) _files.DeleteAsync(key).GetAwaiter().GetResult();
                return true;
            });
        }

        /// <summary>
        /// Seals with the Keystore key: <c>[format 1][iv length][iv][ciphertext + GCM tag]</c>, the value's key name as
        /// GCM additional data (a sealed value copied under another key does not open).
        /// </summary>
        private sealed class KeystoreProtector : ISvBlobProtector
        {
            // Made once per process and reused (each lookup is a round trip to the Keystore service; on a Quest 3 every
            // sealed read or write cost ~13-16 ms on the main thread without them): the key handle of each alias, one
            // Cipher, and the JNI class and method ids. Only touched under Gate, on Unity's main thread.
            private static readonly Dictionary<string, IntPtr> KeyRefs = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            private static IntPtr _cipher;
            private static IntPtr _initKey;
            private static IntPtr _initKeySpec;
            private static IntPtr _updateAad;
            private static IntPtr _doFinal;
            private static IntPtr _getIv;
            private static IntPtr _gcmClass;
            private static IntPtr _gcmCtor;

            private readonly string _alias;
            private readonly SvLogger _log;

            internal KeystoreProtector(string alias, SvLogger log)
            {
                _alias = alias;
                _log = log;
            }

            public byte[] Protect(byte[] data, string key)
            {
                var sealedBytes = Crypt(EncryptMode, data, null, Aad(key), true, out var iv);
                if (iv == null || iv.Length < 12 || iv.Length > 255) throw new SvSecureStoreException("The Keystore gave no usable IV.");
                var blob = new byte[2 + iv.Length + sealedBytes.Length];
                blob[0] = Format;
                blob[1] = (byte)iv.Length;
                Buffer.BlockCopy(iv, 0, blob, 2, iv.Length);
                Buffer.BlockCopy(sealedBytes, 0, blob, 2 + iv.Length, sealedBytes.Length);
                return blob;
            }

            public byte[] Unprotect(byte[] blob, string key)
            {
                if (blob == null || blob.Length < 2 || blob[0] != Format) return null;
                var ivLength = blob[1];
                if (ivLength < 12 || blob.Length < 2 + ivLength + 16) return null;
                var iv = new byte[ivLength];
                Buffer.BlockCopy(blob, 2, iv, 0, ivLength);
                var sealedBytes = new byte[blob.Length - 2 - ivLength];
                Buffer.BlockCopy(blob, 2 + ivLength, sealedBytes, 0, sealedBytes.Length);
                try
                {
                    return Crypt(DecryptMode, sealedBytes, iv, Aad(key), false, out _);
                }
                catch (Exception e)
                {
                    _log?.Invoke(SvLogLevel.Warning, "[SplatterVault] A stored value could not be opened with the Keystore key (" + e.GetType().Name + ").");
                    return null;
                }
            }

            private byte[] Aad(string key)
            {
                return System.Text.Encoding.UTF8.GetBytes(_alias + "|" + key);
            }

            /// <summary>
            /// AES-GCM with the Keystore key. Encrypting lets the Keystore pick the IV (returned in <paramref name="ivOut"/>).
            /// Decrypting never makes a key: a missing key means the value cannot open. The cached key handle and Cipher
            /// are dropped after any Java failure and the call is tried once more with fresh ones (a key invalidated with
            /// KeyPermanentlyInvalidatedException, replaced or deleted); a second failure is the answer (a read: the value
            /// is unreadable; a write: it fails), as without the cache.
            /// </summary>
            private byte[] Crypt(int mode, byte[] input, byte[] iv, byte[] aad, bool createKey, out byte[] ivOut)
            {
                try
                {
                    return CryptOnce(mode, input, iv, aad, createKey, out ivOut);
                }
                catch (Exception)
                {
                    Forget(_alias);
                    return CryptOnce(mode, input, iv, aad, createKey, out ivOut);
                }
            }

            private byte[] CryptOnce(int mode, byte[] input, byte[] iv, byte[] aad, bool createKey, out byte[] ivOut)
            {
                var key = KeyRef(createKey);
                var cipher = CipherRef();
                if (mode == EncryptMode)
                {
                    Jni.CallVoid(cipher, _initKey, "init", new jvalue { i = mode }, new jvalue { l = key });
                }
                else
                {
                    var ivArray = AndroidJNI.ToSByteArray(Jni.ToSigned(iv));
                    try
                    {
                        var spec = AndroidJNI.NewObject(_gcmClass, _gcmCtor, new[] { new jvalue { i = GcmTagBits }, new jvalue { l = ivArray } });
                        Jni.ThrowIfException("GCMParameterSpec");
                        try
                        {
                            Jni.CallVoid(cipher, _initKeySpec, "init", new jvalue { i = mode }, new jvalue { l = key }, new jvalue { l = spec });
                        }
                        finally
                        {
                            AndroidJNI.DeleteLocalRef(spec);
                        }
                    }
                    finally
                    {
                        AndroidJNI.DeleteLocalRef(ivArray);
                    }
                }
                Jni.CallVoidBytes(cipher, _updateAad, "updateAAD", aad);
                var output = Jni.CallBytes(cipher, _doFinal, "doFinal", input);
                ivOut = mode == EncryptMode ? Jni.CallBytes(cipher, _getIv, "getIV", null) : iv;
                return output;
            }

            /// <summary>The process's Cipher ("AES/GCM/NoPadding", re-initialised for every call) and its method ids.</summary>
            private static IntPtr CipherRef()
            {
                if (_cipher != IntPtr.Zero) return _cipher;
                using (var cipherClass = new AndroidJavaClass("javax.crypto.Cipher"))
                using (var cipher = cipherClass.CallStatic<AndroidJavaObject>("getInstance", Transformation))
                {
                    var global = AndroidJNI.NewGlobalRef(cipher.GetRawObject());
                    var cls = AndroidJNI.GetObjectClass(global);
                    try
                    {
                        _initKey = Jni.MethodId(cls, "init", "(ILjava/security/Key;)V");
                        _initKeySpec = Jni.MethodId(cls, "init", "(ILjava/security/Key;Ljava/security/spec/AlgorithmParameterSpec;)V");
                        _updateAad = Jni.MethodId(cls, "updateAAD", "([B)V");
                        _doFinal = Jni.MethodId(cls, "doFinal", "([B)[B");
                        _getIv = Jni.MethodId(cls, "getIV", "()[B");
                    }
                    catch (Exception)
                    {
                        AndroidJNI.DeleteGlobalRef(global);
                        throw;
                    }
                    finally
                    {
                        AndroidJNI.DeleteLocalRef(cls);
                    }
                    if (_gcmClass == IntPtr.Zero)
                    {
                        var local = AndroidJNI.FindClass("javax/crypto/spec/GCMParameterSpec");
                        Jni.ThrowIfException("FindClass GCMParameterSpec");
                        _gcmClass = AndroidJNI.NewGlobalRef(local);
                        AndroidJNI.DeleteLocalRef(local);
                        _gcmCtor = Jni.MethodId(_gcmClass, "<init>", "(I[B)V");
                    }
                    _cipher = global;
                    return _cipher;
                }
            }

            /// <summary>Drops the cached key handle of <paramref name="alias"/> and the Cipher (made again on the next call).</summary>
            private static void Forget(string alias)
            {
                if (KeyRefs.TryGetValue(alias, out var key))
                {
                    AndroidJNI.DeleteGlobalRef(key);
                    KeyRefs.Remove(alias);
                }
                if (_cipher != IntPtr.Zero)
                {
                    AndroidJNI.DeleteGlobalRef(_cipher);
                    _cipher = IntPtr.Zero;
                }
            }

            /// <summary>The cached global reference to this store's Keystore key; looked up (made on first use when
            /// <paramref name="create"/>) only when not cached yet.</summary>
            private IntPtr KeyRef(bool create)
            {
                if (KeyRefs.TryGetValue(_alias, out var cached)) return cached;
                using (var keyStoreClass = new AndroidJavaClass("java.security.KeyStore"))
                using (var keyStore = keyStoreClass.CallStatic<AndroidJavaObject>("getInstance", "AndroidKeyStore"))
                {
                    var ks = keyStore.GetRawObject();
                    // load(null): the AndroidKeyStore needs no stream or password.
                    Jni.CallVoid(ks, "load", "(Ljava/security/KeyStore$LoadStoreParameter;)V", new jvalue { l = IntPtr.Zero });
                    if (!keyStore.Call<bool>("containsAlias", _alias))
                    {
                        if (!create) throw new SvSecureStoreException("This device's Keystore has no key " + _alias + ".");
                        CreateKey();
                    }
                    var alias = AndroidJNI.NewStringUTF(_alias);
                    try
                    {
                        var local = Jni.CallObject(ks, "getKey", "(Ljava/lang/String;[C)Ljava/security/Key;",
                            new jvalue { l = alias }, new jvalue { l = IntPtr.Zero });
                        if (local == IntPtr.Zero) throw new SvSecureStoreException("The Keystore has no key " + _alias + ".");
                        var global = AndroidJNI.NewGlobalRef(local);
                        AndroidJNI.DeleteLocalRef(local);
                        KeyRefs[_alias] = global;
                        return global;
                    }
                    finally
                    {
                        AndroidJNI.DeleteLocalRef(alias);
                    }
                }
            }

            private void CreateKey()
            {
                using (var builder = new AndroidJavaObject("android.security.keystore.KeyGenParameterSpec$Builder", _alias,
                           PurposeEncrypt | PurposeDecrypt))
                {
                    // Varargs String... are a String[]: pass the array as ONE argument.
                    using (builder.Call<AndroidJavaObject>("setBlockModes", (object)new[] { "GCM" }))
                    using (builder.Call<AndroidJavaObject>("setEncryptionPaddings", (object)new[] { "NoPadding" }))
                    using (builder.Call<AndroidJavaObject>("setKeySize", 256))
                    using (var spec = builder.Call<AndroidJavaObject>("build"))
                    using (var generatorClass = new AndroidJavaClass("javax.crypto.KeyGenerator"))
                    using (var generator = generatorClass.CallStatic<AndroidJavaObject>("getInstance", "AES", "AndroidKeyStore"))
                    {
                        Jni.CallVoid(generator.GetRawObject(), "init", "(Ljava/security/spec/AlgorithmParameterSpec;)V",
                            new jvalue { l = spec.GetRawObject() });
                        using (generator.Call<AndroidJavaObject>("generateKey"))
                        {
                        }
                    }
                }
            }

            /// <summary>JNI calls with explicit signatures; a pending Java exception becomes an InvalidOperationException.</summary>
            private static class Jni
            {
                /// <summary>Calls a void method looked up by name (only off the hot path: key lookup and creation).</summary>
                internal static void CallVoid(IntPtr obj, string name, string signature, params jvalue[] args)
                {
                    CallVoid(obj, Method(obj, name, signature), name, args);
                }

                internal static IntPtr CallObject(IntPtr obj, string name, string signature, params jvalue[] args)
                {
                    var result = AndroidJNI.CallObjectMethod(obj, Method(obj, name, signature), args);
                    ThrowIfException(name);
                    return result;
                }

                /// <summary>Calls a void method by its cached id.</summary>
                internal static void CallVoid(IntPtr obj, IntPtr method, string name, params jvalue[] args)
                {
                    AndroidJNI.CallVoidMethod(obj, method, args);
                    ThrowIfException(name);
                }

                /// <summary>Calls a void method (cached id) that takes one byte[].</summary>
                internal static void CallVoidBytes(IntPtr obj, IntPtr method, string name, byte[] input)
                {
                    var arg = AndroidJNI.ToSByteArray(ToSigned(input));
                    try
                    {
                        AndroidJNI.CallVoidMethod(obj, method, new[] { new jvalue { l = arg } });
                        ThrowIfException(name);
                    }
                    finally
                    {
                        AndroidJNI.DeleteLocalRef(arg);
                    }
                }

                /// <summary>Calls a method (cached id) that takes one byte[] (or nothing, when <paramref name="input"/> is null) and returns a byte[].</summary>
                internal static byte[] CallBytes(IntPtr obj, IntPtr method, string name, byte[] input)
                {
                    var arg = input == null ? IntPtr.Zero : AndroidJNI.ToSByteArray(ToSigned(input));
                    try
                    {
                        var args = input == null ? new jvalue[0] : new[] { new jvalue { l = arg } };
                        var result = AndroidJNI.CallObjectMethod(obj, method, args);
                        ThrowIfException(name);
                        if (result == IntPtr.Zero) throw new InvalidOperationException(name + " returned null.");
                        try
                        {
                            return ToUnsigned(AndroidJNI.FromSByteArray(result));
                        }
                        finally
                        {
                            AndroidJNI.DeleteLocalRef(result);
                        }
                    }
                    finally
                    {
                        if (arg != IntPtr.Zero) AndroidJNI.DeleteLocalRef(arg);
                    }
                }

                /// <summary>A method id of <paramref name="cls"/>; throws when there is none.</summary>
                internal static IntPtr MethodId(IntPtr cls, string name, string signature)
                {
                    var method = AndroidJNI.GetMethodID(cls, name, signature);
                    ThrowIfException(name);
                    if (method == IntPtr.Zero) throw new InvalidOperationException("No Java method " + name + signature + ".");
                    return method;
                }

                internal static sbyte[] ToSigned(byte[] bytes)
                {
                    var s = new sbyte[bytes.Length];
                    Buffer.BlockCopy(bytes, 0, s, 0, bytes.Length);
                    return s;
                }

                private static byte[] ToUnsigned(sbyte[] bytes)
                {
                    var u = new byte[bytes.Length];
                    Buffer.BlockCopy(bytes, 0, u, 0, bytes.Length);
                    return u;
                }

                private static IntPtr Method(IntPtr obj, string name, string signature)
                {
                    var cls = AndroidJNI.GetObjectClass(obj);
                    try
                    {
                        return MethodId(cls, name, signature);
                    }
                    finally
                    {
                        AndroidJNI.DeleteLocalRef(cls);
                    }
                }

                internal static void ThrowIfException(string name)
                {
                    var error = AndroidJNI.ExceptionOccurred();
                    if (error == IntPtr.Zero) return;
                    AndroidJNI.ExceptionClear();
                    AndroidJNI.DeleteLocalRef(error);
                    throw new InvalidOperationException("Java exception in " + name + ".");
                }
            }
        }
    }
}
#endif
