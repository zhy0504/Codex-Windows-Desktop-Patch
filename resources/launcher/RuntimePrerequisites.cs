using System;
using System.Globalization;
using Microsoft.Win32;

namespace CodexPatch.NativeLauncher
{
    internal static class RuntimePrerequisites
    {
        internal const int MinimumDotNetFrameworkRelease = 528040;
        internal const string MinimumDotNetFrameworkVersion = "4.8";
        private const string DotNetFrameworkSetupKey = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full";

        internal static int GetInstalledDotNetFrameworkRelease()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(DotNetFrameworkSetupKey, false))
                {
                    if (key == null) return 0;
                    object value = key.GetValue("Release");
                    return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                return 0;
            }
        }

        internal static bool IsSupportedDotNetFrameworkRelease(int release)
        {
            return release >= MinimumDotNetFrameworkRelease;
        }

        internal static void EnsureSupported()
        {
            int release = GetInstalledDotNetFrameworkRelease();
            if (IsSupportedDotNetFrameworkRelease(release)) return;
            throw new PlatformNotSupportedException(
                "Codex Desktop Patch 需要 Microsoft .NET Framework " +
                MinimumDotNetFrameworkVersion +
                " 或更高版本。请通过 Windows Update 安装或修复 .NET Framework 后重试。");
        }
    }
}
