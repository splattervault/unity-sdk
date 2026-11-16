#if UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX || UNITY_ANDROID || SV_UNIX_PERMISSIONS_TESTS
using System;
using System.Runtime.InteropServices;

namespace SplatterVault.Players.Unity
{
    /// <summary>
    /// Owner-only modes (0700 directories, 0600 files) for the encrypted-file store on macOS, Linux and Android, by
    /// calling the C library's <c>chmod</c> (netstandard2.1 and Unity's .NET 4.x profile have no API for it). A
    /// platform where no C library answers is logged once and left as it is.
    /// </summary>
    internal static class SvUnixPermissions
    {
        private const uint DirectoryMode = 0x1C0; // 0700
        private const uint FileMode = 0x180; // 0600
        private static int _which = -1; // -1 unknown, 0 glibc, 1 Darwin, 2 "libc", 3 none
        private static bool _warned;

        /// <summary>Restricts <paramref name="path"/> to its owner. True when the mode was set.</summary>
        internal static bool Restrict(string path, bool isDirectory)
        {
            var mode = isDirectory ? DirectoryMode : FileMode;
            for (var attempt = _which < 0 ? 0 : _which; attempt < 3; attempt++)
            {
                try
                {
                    int rc;
                    switch (attempt)
                    {
                        case 0:
                            rc = ChmodGlibc(path, mode);
                            break;
                        case 1:
                            rc = ChmodDarwin(path, mode);
                            break;
                        default:
                            rc = ChmodLibc(path, mode);
                            break;
                    }
                    _which = attempt;
                    if (rc == 0) return true;
                    Warn("chmod failed (errno " + Marshal.GetLastWin32Error() + ")");
                    return false;
                }
                catch (DllNotFoundException)
                {
                }
                catch (EntryPointNotFoundException)
                {
                }
                if (_which >= 0) break;
            }
            _which = 3;
            Warn("no C library for chmod");
            return false;
        }

        private static void Warn(string why)
        {
            if (_warned) return;
            _warned = true;
            SvPlayersDefaults.Logger?.Invoke(SplatterVault.Core.SvLogLevel.Warning,
                "[SplatterVault] Could not make the secure-store files owner-only (" + why + ").");
        }

        [DllImport("libc.so.6", EntryPoint = "chmod", SetLastError = true)]
        private static extern int ChmodGlibc(string path, uint mode);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "chmod", SetLastError = true)]
        private static extern int ChmodDarwin(string path, uint mode);

        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        private static extern int ChmodLibc(string path, uint mode);
    }
}
#endif
