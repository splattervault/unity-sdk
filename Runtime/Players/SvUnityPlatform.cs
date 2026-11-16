using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SplatterVault.Core;
using UnityEngine;

namespace SplatterVault.Players.Unity
{
    /// <summary>
    /// Plugs Unity into <see cref="SvPlayers"/> when the game starts: the UnityWebRequest transport, the platform's
    /// secure storage and Debug.Log. Nothing to call: <c>new SvPlayers(gameId)</c> just works. Call
    /// <see cref="Install"/> yourself only from an editor tool that runs outside Play mode before the editor hook ran.
    /// </summary>
    public static class SvUnityPlatform
    {
        internal const string WebGlMessage =
            "SplatterVault player services do not work in WebGL builds: browsers block the SplatterVault API (CORS). " +
            "Build for a native platform (Windows, macOS, Linux, Android, Meta Quest, iOS).";

        private static bool _warnedFileStore;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void OnSubsystemRegistration()
        {
            Install();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void OnBeforeSceneLoad()
        {
            // Unity's SynchronizationContext is surely in place by now.
            SvUnityMainThread.Capture();
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void OnEditorLoad()
        {
            Install();
        }
#endif

        /// <summary>Registers the Unity defaults (call it on the main thread). Safe to call more than once.</summary>
        public static void Install()
        {
            SvUnityMainThread.Capture();
            SvPlayersDefaults.Logger = UnityLog;
#if UNITY_WEBGL && !UNITY_EDITOR
            SvPlayersDefaults.UnsupportedPlatformMessage = WebGlMessage;
            SvPlayersDefaults.TransportFactory = null;
            SvPlayersDefaults.SecureStoreFactory = null;
#else
            SvPlayersDefaults.UnsupportedPlatformMessage = null;
            SvPlayersDefaults.TransportFactory = () => new UnityWebRequestTransport();
            SvPlayersDefaults.SecureStoreFactory = CreateDefaultSecureStore;
#endif
        }

        /// <summary>
        /// This platform's secure storage: Android Keystore (Android 6+, Meta Quest), iOS Keychain, Windows DPAPI;
        /// elsewhere (macOS, Linux, older Android, the Editor on macOS and Linux) an encrypted file, owner-only
        /// (0600), with a warning. macOS players do not use the Keychain yet (it needs a native macOS plugin): plug
        /// your own <see cref="ISecureStore"/> in for that.
        /// </summary>
        public static ISecureStore CreateDefaultSecureStore(string name = "splattervault")
        {
            var safeName = SafeName(name);
#if (UNITY_IOS || UNITY_TVOS) && !UNITY_EDITOR
            return new IosKeychainSecureStore("com.splattervault.sdk." + safeName);
#elif UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            return new SvProtectedFileStore(StoreDirectory(safeName), new WindowsDpapiProtector("SplatterVault:" + safeName), UnityLog);
#else
#if UNITY_ANDROID && !UNITY_EDITOR
            bool keystore;
            try
            {
                keystore = AndroidKeystoreSecureStore.IsSupported;
            }
            catch (Exception e)
            {
                UnityLog(SvLogLevel.Error, "[SplatterVault] Could not check for the Android Keystore (" + e.GetType().Name + ").");
                return new SvUnreadableSecureStore("The Android version could not be read.", e);
            }
            if (keystore)
            {
                try
                {
                    return new AndroidKeystoreSecureStore(safeName, UnityLog);
                }
                catch (Exception e)
                {
                    // Never another, empty store instead: what this device stored would read as "nothing" and a new guest
                    // would be made over the stored one. Unreadable until it opens (next launch).
                    UnityLog(SvLogLevel.Error, "[SplatterVault] The Android Keystore store could not be opened (" + e.GetType().Name + ").");
                    return new SvUnreadableSecureStore("The Android Keystore store could not be opened.", e);
                }
            }
            UnityLog(SvLogLevel.Warning, "[SplatterVault] Android Keystore needs Android 6 or later.");
#endif
            return CreateEncryptedFileStore(safeName);
#endif
        }

        /// <summary>
        /// The fallback: values encrypted with a key derived from this app and device and a random salt, in owner-only
        /// files (0700 directory, 0600 files on macOS, Linux and Android; on Android in the app's no-backup directory,
        /// never on external storage and never in a backup). It keeps them from being read as plain text; it does not
        /// protect them from someone who can read the app's files on this device. A value sealed with another salt or
        /// device id reads as an error (<c>storage_unreadable</c>), never as missing.
        /// </summary>
        public static ISecureStore CreateEncryptedFileStore(string name = "splattervault")
        {
            var dir = Path.Combine(FallbackRoot(), "SplatterVault", SafeName(name));
            if (!_warnedFileStore)
            {
                _warnedFileStore = true;
                UnityLog(SvLogLevel.Warning,
                    "[SplatterVault] No platform secure storage here: refresh tokens and guest secrets are kept in an encrypted, owner-only file. " +
                    "Plug in your own ISecureStore (SvPlayersOptions.SecureStore) for stronger protection.");
            }
            var restrict = Restrictor();
            var salt = SvAesBlobProtector.LoadOrCreateSalt(Path.Combine(dir, "store.salt"), restrict);
            var material = Application.identifier + "|" + SystemInfo.deviceUniqueIdentifier;
            return new SvProtectedFileStore(dir, new SvAesBlobProtector(material, salt), UnityLog, restrict);
        }

        private static Action<string, bool> Restrictor()
        {
#if UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX || UNITY_ANDROID
            return (path, isDirectory) => SvUnixPermissions.Restrict(path, isDirectory);
#else
            return null;
#endif
        }

        /// <summary>
        /// Application.persistentDataPath, except on Android: the app's no-backup files directory, which Auto Backup and
        /// device transfers never copy (a value sealed with this device's id could not be opened on another device).
        /// </summary>
        private static string FallbackRoot()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                return AndroidKeystoreSecureStore.NoBackupDirectory();
            }
            catch (Exception e)
            {
                UnityLog(SvLogLevel.Warning, "[SplatterVault] No no-backup files directory (" + e.GetType().Name + "); using persistentDataPath.");
            }
#endif
            return Application.persistentDataPath;
        }

        private static string StoreDirectory(string name)
        {
            return Path.Combine(Application.persistentDataPath, "SplatterVault", name);
        }

        private static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "splattervault";
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.'))
                    chars[i] = '_';
            }
            return new string(chars);
        }

        internal static void UnityLog(SvLogLevel level, string message)
        {
            switch (level)
            {
                case SvLogLevel.Error:
                    Debug.LogError(message);
                    break;
                case SvLogLevel.Warning:
                    Debug.LogWarning(message);
                    break;
                case SvLogLevel.Info:
                    Debug.Log(message);
                    break;
                default:
                    // Debug lines (retries) stay quiet unless the game logs them itself.
                    break;
            }
        }
    }

    /// <summary>Runs work on Unity's main thread (UnityWebRequest and the Android JNI calls need it).</summary>
    internal static class SvUnityMainThread
    {
        private static int _mainThreadId = -1;
        private static SynchronizationContext _context;

        internal static void Capture()
        {
            var current = SynchronizationContext.Current;
            if (current == null) return;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _context = current;
        }

        internal static bool IsMainThread => _mainThreadId == -1 || Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>Runs <paramref name="action"/> now on the main thread, else posts it there.</summary>
        internal static void Run(Action action)
        {
            if (_context == null || IsMainThread)
            {
                action();
                return;
            }
            _context.Post(_ => action(), null);
        }

        /// <summary>Runs <paramref name="work"/> on the main thread (now when already there) and hands back its result or exception.</summary>
        internal static Task<T> RunAsync<T>(Func<T> work)
        {
            if (_context == null || IsMainThread)
            {
                try
                {
                    return Task.FromResult(work());
                }
                catch (Exception e)
                {
                    return Task.FromException<T>(e);
                }
            }
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _context.Post(_ =>
            {
                try
                {
                    tcs.TrySetResult(work());
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
            }, null);
            return tcs.Task;
        }
    }
}
