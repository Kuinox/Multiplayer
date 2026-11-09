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
