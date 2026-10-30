using System;
using Microsoft.Win32;

namespace IslandEye
{
    /// <summary>Adds/removes the app from the current user's Windows startup list.</summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Island";
        private const string LegacyName = "IslandEye";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }

        public static void Set(bool enable)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            key.DeleteValue(LegacyName, throwOnMissingValue: false);
            if (enable) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
