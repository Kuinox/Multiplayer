#nullable enable
using Multiplayer.Common;
using Verse;

namespace Multiplayer.Client.Util;

// Update checking itself lives in AutoUpdater.
public static class VersionChecker
{
    public static bool IsContinuousRelease =>
        Multiplayer.modContentPack.ModMetaData.Source == ContentSource.ModsFolder &&
        (AutoUpdater.Repository != null || MpVersion.GitDescription?.StartsWith("continuous") == true) &&
        MpVersion.GitHash?.EndsWith("dirty") == false;

    public static bool IsLocalBuild =>
        Multiplayer.modContentPack.ModMetaData.Source == ContentSource.ModsFolder &&
        MpVersion.GitHash?.EndsWith("dirty") == true;
}
