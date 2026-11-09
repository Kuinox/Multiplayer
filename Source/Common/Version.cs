using System.Linq;
using System.Reflection;

namespace Multiplayer.Common
{
    public static class MpVersion
    {
        public const string SimpleVersion = "0.11.5";
        public const int Protocol = 57;

        public static readonly string? GitHash = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attr => attr.Key == "GitHash")?.Value;

        public static readonly string Version = SimpleVersion + (GitHash != null ? $"+{GitHash}" : "");

        public static readonly string? GitDescription = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attr => attr.Key == "GitDescription")?.Value;

        /// A hash produced by a clean CI/commit build (not "-dirty"/"-broken" and not missing).
        public static bool IsReleaseHash(string? hash) =>
            hash != null && System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-fA-F]{7,40}$");

        /// git prints abbreviated hashes longer than 7 chars when 7 would be ambiguous in that clone, so builds of the
        /// same commit can report "abc1234" or "abc1234f". Compare on the common prefix.
        public static bool SameBuild(string? a, string? b)
        {
            if (a == null || b == null) return false;
            var len = System.Math.Min(a.Length, b.Length);
            return len >= 7 && string.Compare(a, 0, b, 0, len, System.StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// Release tags always use the 7 char prefix.
        public static string ShortHash(string hash) => hash.Substring(0, System.Math.Min(7, hash.Length));

        /// Extracts the git hash from a version string like "0.11.5+abc1234".
        public static string? HashFromVersion(string? version)
        {
            var plus = version?.IndexOf('+') ?? -1;
            return plus < 0 ? null : version!.Substring(plus + 1);
        }

        public const string ApiAssemblyName = "0MultiplayerAPI";

#if DEBUG
        public const bool IsDebug = true;
#else
        public const bool IsDebug = false;
#endif
    }
}
