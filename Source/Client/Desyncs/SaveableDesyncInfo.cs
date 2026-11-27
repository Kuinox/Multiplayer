using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;
using Multiplayer.Common;
using Multiplayer.Common.Util;
using RimWorld;
using UnityEngine;
using Verse;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace Multiplayer.Client.Desyncs;

public class SaveableDesyncInfo(
    SyncCoordinator coordinator,
    ClientSyncOpinion local,
    ClientSyncOpinion remote,
    int diffAt,
    bool diffAtFound)
{
    public readonly ClientSyncOpinion local = local;
    public readonly ClientSyncOpinion remote = remote;
    public readonly int diffAt = diffAt;
    private readonly Task<string> metadata = Task.Run(MetadataGenerator.Generate);
    private readonly Task<FileInfo> replay = Task.Run(SaveReplayIfApplicable);

    // The last desync of this session: the host's info is routed here whenever it arrives
    [CanBeNull] public static SaveableDesyncInfo Current { get; private set; }

    [CanBeNull] public HostInfo Host { get; private set; }
    public bool Saved { get; private set; }
    [CanBeNull] private string savedPath;

    public bool ReadyToSave => metadata.IsCompleted && replay.IsCompleted;

    public SaveableDesyncInfo Register()
    {
        Current = this;
        return this;
    }

    public void HandleHostInfo(HostInfo hostInfo)
    {
        Host = hostInfo;
        if (Saved) AddHostInfoToSavedZip();
    }

    // The zip was written without the host's info (it came after the wait in DesyncedWindow): add it now
    private void AddHostInfoToSavedZip()
    {
        if (savedPath == null || Host == null) return;

        try
        {
            using var zip = MpZipFile.Open(savedPath, ZipArchiveMode.Update);
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("host_")).ToList())
                entry.Delete();
            AddHostEntries(zip, Host);
            Log.Message($"Multiplayer: added the host's desync info to {Path.GetFileName(savedPath)}");
        }
        catch (Exception e)
        {
            Log.Error($"Exception adding host desync info to {savedPath}: {e}");
        }
    }

    private static void AddHostEntries(ZipArchive zip, [CanBeNull] HostInfo hostInfo)
    {
        zip.AddEntry("host_traces.txt", hostInfo?.Traces ?? "No host traces");
        zip.AddEntry("host_jitted_methods.txt", hostInfo?.JittedMethods ?? "No host jitted methods");
        zip.AddEntry("host_pawn_state.txt", hostInfo?.PawnState ?? "No host pawn state");
        zip.AddEntry("host_pawn_floats.txt", hostInfo?.PawnFloats ?? "No host pawn floats");
    }

    // Needs the game loaded (pawn dumps, session): call it before leaving the game
    public void Save()
    {
        if (Saved) return;
        Saved = true;

        var watch = Stopwatch.StartNew();

        try
        {
            var desyncFilePath = FindFileNameForNextDesyncFile();
            savedPath = Path.Combine(Multiplayer.DesyncsDir, desyncFilePath + ".zip");
            using var zip = MpZipFile.Open(savedPath, ZipArchiveMode.Create);

            zip.AddEntry("desync_info", GetDesyncDetails());

            zip.AddEntry("local_traces.txt", GetLocalTraces());
            zip.AddEntry("local_jitted_methods.txt", JittedMethods.GetJittedMethodsString());
            AddHostEntries(zip, Host);

            var extraLogs = LogGenerator.PrepareLogData();
            if (extraLogs != null) zip.AddEntry("local_logs.txt", extraLogs);

            zip.AddEntry("local_metadata.txt", metadata.Result);

            try
            {
                zip.AddEntry("local_pawn_state.txt", DesyncStateDump.PawnState());
            }
            catch (Exception e)
            {
                zip.AddEntry("local_pawn_state.txt", $"Failed: {e}");
            }

            try
            {
                zip.AddEntry("local_pawn_floats.txt", PawnFloatState.Dump());
            }
            catch (Exception e)
            {
                zip.AddEntry("local_pawn_floats.txt", $"Failed: {e}");
            }

            try
            {
                replay.Wait();
            }
            catch (AggregateException e)
            {
                if (e.InnerExceptions.SingleOrDefault(inner => inner is TaskCanceledException) == null) throw;
            }
            if (replay.IsCompletedSuccessfully) {
                var replayFile = replay.Result;
                zip.CreateEntryFromFile(replayFile.FullName, "replay.rwmts", CompressionLevel.NoCompression);
                DeleteFileSilent(replayFile);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Exception writing desync info: {e}");
        }

        Log.Message($"Desync info writing took {watch.ElapsedMilliseconds}");
    }

    private string GetLocalTraces()
    {
        var traceMessage = "";
        var localCount = local.desyncStackTraceHashes.Count;
        var remoteCount = remote.desyncStackTraceHashes.Count;
        int count = Math.Min(localCount, remoteCount);

        if (diffAt == -1)
        {
            if (count == 0)
            {
                return $"No traces (remote: {remoteCount}, local: {localCount})";
            }

            traceMessage = "Note: trace hashes are equal between local and remote\n\n";
        }
        else if (!diffAtFound)
        {
            traceMessage = "Note: traces differ in amount, but the existing ones are equal. This means that a tick" +
                           " has ended sooner on one of the connection sides\n\n";
        }

        traceMessage += local.GetFormattedStackTracesForRange(diffAt);
        return traceMessage;
    }

    private string GetDesyncDetails()
    {
        var desyncInfo = new StringBuilder();

        desyncInfo
            .AppendLine("###Tick Data###")
            .AppendLine($"Arbiter Connected And Playing|||{Multiplayer.session.ArbiterPlaying}")
            .AppendLine($"Last Valid Tick - Local|||{coordinator.lastValidTick}")
            .AppendLine($"Last Valid Tick - Arbiter|||{coordinator.arbiterWasPlayingOnLastValidTick}")
            .AppendLine("\n###Version Data###")
            .AppendLine($"Multiplayer Mod Version|||{MpVersion.Version}")
            .AppendLine($"Rimworld Version and Rev|||{VersionControl.CurrentVersionStringWithRev}")
            .AppendLine("\n###Debug Options###")
            .AppendLine($"Multiplayer Debug Build - Client|||{MpVersion.IsDebug}")
            .AppendLine($"Multiplayer Debug Mode - Host|||{Multiplayer.GameComp.debugMode}")
            .AppendLine($"Rimworld Developer Mode - Client|||{Prefs.DevMode}")
            .AppendLine("\n###Debug Settings - Client###")
            .Append(DesyncStateDump.DebugSettingsSection())
            .AppendLine("\n###Server Info###")
            .AppendLine($"Player Count|||{Multiplayer.session.players.Count}")
            .AppendLine($"Async time active|||{Multiplayer.GameComp.asyncTime}")
            .AppendLine($"Multifaction active|||{Multiplayer.GameComp.multifaction}")
            .AppendLine($"Map Count|||{Find.Maps?.Count.ToStringSafe()}")
            .AppendLine("\n###CPU Info###")
            .AppendLine($"Processor Name|||{SystemInfo.processorType}")
            .AppendLine($"Processor Speed (MHz)|||{SystemInfo.processorFrequency}")
            .AppendLine($"Thread Count|||{SystemInfo.processorCount}")
            .AppendLine("\n###GPU Info###")
            .AppendLine($"GPU Family|||{SystemInfo.graphicsDeviceVendor}")
            .AppendLine($"GPU Type|||{SystemInfo.graphicsDeviceType}")
            .AppendLine($"GPU Name|||{SystemInfo.graphicsDeviceName}")
            .AppendLine($"GPU VRAM|||{SystemInfo.graphicsMemorySize}")
            .AppendLine("\n###RAM Info###")
            .AppendLine($"Physical Memory Present|||{SystemInfo.systemMemorySize}")
            .AppendLine("\n###OS Info###")
            .AppendLine($"OS Type|||{SystemInfo.operatingSystemFamily}")
            .AppendLine($"OS Name and Version|||{SystemInfo.operatingSystem}");

        return desyncInfo.ToString();
    }

    private static string FindFileNameForNextDesyncFile()
    {
        const string FilePrefix = "Desync-";
        const string FileExtension = ".zip";

        //Find all current existing desync zips
        var files = new DirectoryInfo(Multiplayer.DesyncsDir).GetFiles($"{FilePrefix}*{FileExtension}");

        const int MaxFiles = 10;

        //Delete any pushing us over the limit, and reserve room for one more
        if (files.Length > MaxFiles - 1)
            files.OrderByDescending(f => f.LastWriteTime).Skip(MaxFiles - 1).Do(DeleteFileSilent);

        //Find the current max desync number
        int max = 0;
        foreach (var f in files)
            if (int.TryParse(
                    f.Name.Substring(FilePrefix.Length, f.Name.Length - FilePrefix.Length - FileExtension.Length),
                    out int result) && result > max)
                max = result;

        return $"{FilePrefix}{max + 1:00}";
    }

    private static Task<FileInfo> SaveReplayIfApplicable()
    {
        if (!Multiplayer.settings.includeReplayInDesync) return Task.FromCanceled<FileInfo>(new CancellationToken(true));

        try
        {
            var tmp = new FileInfo(Path.Combine(Multiplayer.DesyncsDir, "desync-replay.tmp.zip"));
            if (tmp.Exists) tmp.Delete();
            Replay.ForSaving(tmp).WriteData(Multiplayer.session.dataSnapshot);
            return Task.FromResult(tmp);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save replay for desync: {e}");
            return Task.FromException<FileInfo>(e);
        }
    }

    private static void DeleteFileSilent(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (IOException)
        {
        }
    }

    public record HostInfo([CanBeNull] string Traces, [CanBeNull] string JittedMethods,
        [CanBeNull] string PawnState = null, [CanBeNull] string PawnFloats = null);
}
