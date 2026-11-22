using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Desyncs;

// Extra local state written into desync reports. Everything here only reads fields: no stat/thought/job calls
// (those can write caches and desync on their own) and no Rand or stack-trace records, so it leaves the trace
// hashes untouched and a Release build stays compatible with the host's CI build.
public static class DesyncStateDump
{
    private static readonly FieldInfo[] DebugSettingsFields = typeof(DebugSettings)
        .GetFields(BindingFlags.Static | BindingFlags.Public)
        .Where(f => f.FieldType == typeof(bool) && !f.IsLiteral)
        .ToArray();

    private static bool[] debugSettingsSnapshot;

    // Dev-mode debug settings are local, unsynced, and some of them gate simulation code
    // (e.g. enableRandomMentalStates gates MentalBreaker.MentalBreakerTickInterval)
    public static string DebugSettingsSection()
    {
        var sb = new StringBuilder();
        foreach (var f in DebugSettingsFields)
            sb.AppendLine($"{f.Name}|||{f.GetValue(null)}");
        return sb.ToString();
    }

    // Logs every change to a DebugSettings bool while in a multiplayer game, with the tick, so a desync report's
    // log tells whether something was toggled before it
    public static void WatchDebugSettings()
    {
        if (Multiplayer.Client == null)
        {
            debugSettingsSnapshot = null;
            return;
        }

        var current = new bool[DebugSettingsFields.Length];
        for (int i = 0; i < DebugSettingsFields.Length; i++)
            current[i] = (bool)DebugSettingsFields[i].GetValue(null);

        if (debugSettingsSnapshot != null)
            for (int i = 0; i < current.Length; i++)
                if (current[i] != debugSettingsSnapshot[i])
                    Log.Warning($"MP: DebugSettings.{DebugSettingsFields[i].Name} changed {debugSettingsSnapshot[i]} -> {current[i]} at timer {TickPatch.Timer}");

        debugSettingsSnapshot = current;
    }

    public static string PawnState()
    {
        var sb = new StringBuilder();
        var breakThresholdCache = StatDefOf.MentalBreakThreshold.Worker.temporaryStatCache;

        foreach (var map in Find.Maps)
        {
            sb.AppendLine($"=== Map {map.uniqueID} owner={map.ParentFaction?.Name} mapTicks={map.AsyncTime()?.mapTicks}");

            foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (pawn.needs?.mood == null) continue;
                try
                {
                    AppendPawn(sb, pawn, breakThresholdCache);
                }
                catch (Exception e)
                {
                    sb.AppendLine($"{pawn.ThingID} dump failed: {e.GetType().Name} {e.Message}");
                }
            }
        }

        return sb.ToString();
    }

    private static void AppendPawn(StringBuilder sb, Pawn pawn, Dictionary<Thing, StatCacheEntry> breakThresholdCache)
    {
        var mood = pawn.needs.mood;
        var breaker = pawn.mindState?.mentalBreaker;

        sb.Append($"{pawn.ThingID} '{pawn.Name?.ToStringShort}' faction={pawn.Faction?.Name} pos={pawn.Position}");
        sb.Append($" job={pawn.jobs?.curJob?.def?.defName} mentalState={pawn.mindState?.mentalStateHandler?.curStateInt?.def?.defName}");
        sb.AppendLine();

        sb.Append($"  mood={mood.curLevelInt:R} lastInstantMood={mood.lastInstantMood:R} lastInstantMoodCheckTick={mood.lastInstantMoodCheckTick}");
        if (breakThresholdCache != null && breakThresholdCache.TryGetValue(pawn, out var entry))
            sb.Append($" breakThresholdCache={entry.statValue:R}@{entry.gameTick}");
        sb.AppendLine();

        if (breaker != null)
            sb.AppendLine($"  breaker ticksUntilCanBreak={breaker.ticksUntilCanDoMentalBreak} belowExtreme={breaker.ticksBelowExtreme} belowMajor={breaker.ticksBelowMajor} belowMinor={breaker.ticksBelowMinor}");

        var thoughts = mood.thoughts;
        if (thoughts?.situational?.cachedThoughts is { } situational)
            sb.AppendLine("  situational: " + string.Join(", ", situational.Select(t => $"{t.def?.defName}[{t.curStageIndex}]")));
        if (thoughts?.memories?.memories is { } memories)
            sb.AppendLine("  memories: " + string.Join(", ", memories.Select(t => $"{t.def?.defName}[{t.forcedStage}] age={t.age} other={t.otherPawn?.ThingID}")));
        if (pawn.story?.traits?.allTraits is { } traits)
            sb.AppendLine("  traits: " + string.Join(", ", traits.Select(t => $"{t.def?.defName}({t.degree}){(t.suppressedByTrait || t.suppressedByGene != null ? " suppressed" : "")}")));
    }
}

[HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Update))]
static class DebugSettingsWatcherPatch
{
    static void Postfix()
    {
        if (UnityEngine.Time.frameCount % 30 == 0)
            DesyncStateDump.WatchDebugSettings();
    }
}
