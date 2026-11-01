using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IslandEye
{
    /// <summary>
    /// "Start with Windows". Registers Island in the current user's Run key, clears Windows' own
    /// "disabled" flag for it, heals the path if the .exe moved, and removes the "downloaded from the
    /// internet" mark that can stop a program from launching at sign-in.
    /// </summary>
    public static class StartupManager
    {
        public const string AutoStartArg = "--autostart";

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ValueName = "Island";
        private const string LegacyName = "IslandEye";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool DeleteFileW(string path);

        private static string Command => $"\"{Environment.ProcessPath}\" {AutoStartArg}";

        /// <summary>True only if Island is registered AND Windows hasn't disabled it (Task Manager > Startup apps).</summary>
        public static bool IsEnabled()
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                if (run?.GetValue(ValueName) is not string) return false;
                return !IsDisabledByWindows();
            }
            catch { return false; }
        }

        private static bool IsDisabledByWindows()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                if (k?.GetValue(ValueName) is byte[] b && b.Length > 0) return (b[0] & 1) == 1;   // 03 = disabled, 02/06 = enabled
            }
            catch { }
            return false;
        }

        private static void ClearDisabledFlag()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                k?.DeleteValue(ValueName, throwOnMissingValue: false);
                k?.DeleteValue(LegacyName, throwOnMissingValue: false);
            }
            catch { }
        }

        /// <summary>Removes the "downloaded from the internet" mark (it can block auto-start).</summary>
        private static void UnblockSelf()
        {
            try { if (Environment.ProcessPath is string p) DeleteFileW(p + ":Zone.Identifier"); } catch { }
        }

        public static void Set(bool enable)
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.DeleteValue(LegacyName, throwOnMissingValue: false);
            if (enable)
            {
                run.SetValue(ValueName, Command);
                ClearDisabledFlag();
                UnblockSelf();
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }

        /// <summary>
        /// Runs at every launch: if you turned auto-start on, make sure the registry entry still exists
        /// and points at THIS .exe (fixes moved / renamed / updated files and cleaner tools that removed it).
        /// </summary>
        public static void Repair(bool wanted)
        {
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKey);
                string? current = run.GetValue(ValueName) as string;
                if (current == null && !wanted) return;           // never turned on -> leave alone
                if (!string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                    run.SetValue(ValueName, Command);
                run.DeleteValue(LegacyName, throwOnMissingValue: false);
                UnblockSelf();
            }
            catch { }
        }

        /// <summary>One line describing the current auto-start state (goes into log.txt).</summary>
        public static string Describe()
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                string? v = run?.GetValue(ValueName) as string;
                string approved = "no flag";
                using var k = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                if (k?.GetValue(ValueName) is byte[] b && b.Length > 0)
                    approved = (b[0] & 1) == 1 ? "DISABLED in Windows" : "enabled";
                return $"run=[{v ?? "none"}] startupApproved=[{approved}]";
            }
            catch (Exception ex) { return "startup info unavailable: " + ex.Message; }
        }
    }
}
