#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
using System;
using System.Runtime.InteropServices;
using System.Text;
using SplatterVault.Core;

namespace SplatterVault.Players.Unity
{
    /// <summary>
    /// Windows data protection (DPAPI, current user): only this Windows account on this machine can open the
    /// values. Used with <see cref="SvProtectedFileStore"/> under Application.persistentDataPath.
    /// </summary>
    public sealed class WindowsDpapiProtector : ISvBlobProtector
    {
        private const int CryptProtectUiForbidden = 0x1;
        private readonly string _entropyPrefix;

        public WindowsDpapiProtector(string entropy)
        {
            _entropyPrefix = entropy ?? "SplatterVault";
        }

        public byte[] Protect(byte[] data, string key)
        {
            return Run(data, key, true);
        }

        public byte[] Unprotect(byte[] blob, string key)
        {
            try
            {
                return Run(blob, key, false);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private byte[] Run(byte[] input, string key, bool protect)
        {
            var entropy = Encoding.UTF8.GetBytes(_entropyPrefix + "|" + key);
            var inBlob = Pin(input, out var inHandle);
            var entropyBlob = Pin(entropy, out var entropyHandle);
            var outBlob = default(DataBlob);
            try
            {
                var ok = protect
                    ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob);
                if (!ok) throw new InvalidOperationException("DPAPI failed (error " + Marshal.GetLastWin32Error() + ").");
                var output = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, output, 0, outBlob.cbData);
                return output;
            }
            finally
            {
                inHandle.Free();
                entropyHandle.Free();
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static DataBlob Pin(byte[] bytes, out GCHandle handle)
        {
            handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            return new DataBlob { cbData = bytes.Length, pbData = handle.AddrOfPinnedObject() };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob pDataIn, string szDataDescr, ref DataBlob pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
#endif
