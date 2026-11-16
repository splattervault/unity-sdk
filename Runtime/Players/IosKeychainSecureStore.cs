#if (UNITY_IOS || UNITY_TVOS) && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SplatterVault.Core;

namespace SplatterVault.Players.Unity
{
    /// <summary>
    /// iOS Keychain storage (generic passwords, <c>kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly</c>: readable
    /// after the first unlock since boot, never synced to iCloud or restored to another device). Native code:
    /// Runtime/Plugins/iOS/SplatterVaultKeychain.mm (links Security.framework).
    /// </summary>
    public sealed class IosKeychainSecureStore : ISecureStore
    {
        private const int ErrSecSuccess = 0;
        private const int ErrSecItemNotFound = -25300;
        private readonly string _service;

        public IosKeychainSecureStore(string service)
        {
            _service = string.IsNullOrEmpty(service) ? "com.splattervault.sdk" : service;
        }

        /// <summary>
        /// The value; null only for errSecItemNotFound. Any other status (a locked device before its first unlock:
        /// errSecInteractionNotAllowed, -25308) throws: an unreadable keychain is never "no guest".
        /// </summary>
        public Task<string> GetAsync(string key)
        {
            var status = sv_keychain_get(_service, key, out var value, out var length);
            try
            {
                if (status == ErrSecItemNotFound) return Task.FromResult<string>(null);
                if (status != ErrSecSuccess) throw new SvSecureStoreException("Keychain read failed (OSStatus " + status + ").");
                if (value == IntPtr.Zero || length < 0) throw new SvSecureStoreException("Keychain read returned no data.");
                // Marshal.Copy + UTF-8 (PtrToStringUTF8 is missing from Unity's .NET 4.x profile before 2021.2).
                var bytes = new byte[length];
                if (length > 0) Marshal.Copy(value, bytes, 0, length);
                return Task.FromResult(Encoding.UTF8.GetString(bytes));
            }
            finally
            {
                if (value != IntPtr.Zero) sv_keychain_free(value);
            }
        }

        public Task SetAsync(string key, string value)
        {
            if (value == null) return DeleteAsync(key);
            var bytes = Encoding.UTF8.GetBytes(value);
            var status = sv_keychain_set(_service, key, bytes, bytes.Length);
            if (status != ErrSecSuccess) throw new SvSecureStoreException("Keychain write failed (OSStatus " + status + ").");
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key)
        {
            var status = sv_keychain_delete(_service, key);
            if (status != ErrSecSuccess && status != ErrSecItemNotFound)
                throw new SvSecureStoreException("Keychain delete failed (OSStatus " + status + ").");
            return Task.CompletedTask;
        }

        [DllImport("__Internal")]
        private static extern int sv_keychain_set(string service, string account, byte[] value, int length);

        [DllImport("__Internal")]
        private static extern int sv_keychain_get(string service, string account, out IntPtr value, out int length);

        [DllImport("__Internal")]
        private static extern int sv_keychain_delete(string service, string account);

        [DllImport("__Internal")]
        private static extern void sv_keychain_free(IntPtr value);
    }
}
#endif
