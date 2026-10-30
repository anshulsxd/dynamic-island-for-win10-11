using System;
using System.Reflection;

namespace IslandEye
{
    public static class AppInfo
    {
        public const string AppName = "Island";

        /// <summary>
        /// YOUR GitHub repository in "owner/name" form, e.g. "janedoe/Island".
        /// Island looks at the newest *Release* of this repo to find updates.
        /// </summary>
        public const string GitHubRepo = "anshulsxd/YOUR_REPO_NAME";

        public static bool RepoConfigured =>
            !GitHubRepo.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase);

        public static Version Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static string VersionText => Version.ToString(3);
    }
}
