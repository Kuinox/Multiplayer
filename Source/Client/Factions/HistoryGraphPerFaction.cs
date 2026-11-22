using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Factions;

// The History tab graph in multifaction:
// - Its marks are the game's historical tales (raids...), and the TaleManager is shared, so every faction's raids showed.
//   Tales are now tagged with the faction they were recorded for and the graph only shows the viewer's.
// - The faction's history is recorded on world ticks and the tab read the world clock, but the world runs at the fastest
//   map's speed, so the graph went past the colony's own date (and its tales, dated on the map clock, didn't line up).
//   The tab now runs on the player's map clock and the curves are shifted onto it.
static class HistoryGraphPerFaction
{
    // The viewer's own map, whose clock the graph uses. Null outside multifaction or for spectators.
    public static Map PlayerMap()
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return null;

        var me = Multiplayer.RealPlayerFaction;
        if (me == null || me == Multiplayer.WorldComp.spectatorFaction) return null;

        var current = Find.CurrentMap;
        if (current != null && current.ParentFaction == me) return current;
        return Find.Maps.FirstOrDefault(m => m.ParentFaction == me);
    }

    // Map clock minus world clock: how far the curves (recorded on world ticks) shift to land on the map's timeline.
    // Only exact for "now", the drift between the clocks built up over time.
    public static int CurveOffset(Map map)
    {
        if (map == null || !Multiplayer.GameComp.asyncTime) return 0;
        return map.AsyncTime().mapTicks - Multiplayer.AsyncWorldTime.worldTicks;
    }

    public static List<Tale> TalesForGraph(List<Tale> tales)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return tales;

        var me = Multiplayer.RealPlayerFaction;
        if (me == null || me == Multiplayer.WorldComp.spectatorFaction) return tales;

        return tales.Where(t => t.def.type != TaleType.PermanentHistorical || IsFactions(t, me)).ToList();
    }

    private static bool IsFactions(Tale tale, Faction faction)
    {
        foreach (var data in Multiplayer.WorldComp.factionData.Values)
            if (data.historicalTales.Contains(tale.id))
                return data.factionId == faction.loadID;

        // Recorded before tales were tagged
        switch (tale)
        {
            case Tale_SinglePawn single when single.pawnData != null:
                return single.pawnData.faction == faction;
            case Tale_DoublePawn pair when pair.firstPawnData != null || pair.secondPawnData != null:
                return pair.firstPawnData?.faction == faction || pair.secondPawnData?.faction == faction;
        }

        // Pawnless tales (raids) carry no location: use the letter the incident sent on the same tick, if still archived
        foreach (var archivable in Find.Archive.ArchivablesListForReading)
        {
            if (archivable is not Letter { lookTargets: { } targets } letter) continue;

            foreach (var target in targets.targets)
            {
                var map = target.Map;
                if (map == null) continue;
                var startAbs = Multiplayer.GameComp.asyncTime ? map.AsyncTime().GameStartAbsTick : Find.TickManager.gameStartAbsTick;
                if (letter.arrivalTick + startAbs == tale.date)
                    return map.ParentFaction == faction;
            }
        }

        return false;
    }
}

[HarmonyPatch(typeof(TaleManager), nameof(TaleManager.Add))]
static class RecordHistoricalTaleFaction
{
    static void Postfix(Tale tale)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return;
        if (tale?.def?.type != TaleType.PermanentHistorical) return;
        if (!(Multiplayer.Ticking || Multiplayer.ExecutingCmds)) return;

        if (Multiplayer.WorldComp.factionData.TryGetValue(Faction.OfPlayer.loadID, out var data))
            data.historicalTales.Add(tale.id);
    }
}

// The tab's "now", its range buttons and the tale marks use the player's map clock
[HarmonyPatch]
static class HistoryTabMapTime
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MainTabWindow_History), nameof(MainTabWindow_History.PreOpen));
        yield return AccessTools.Method(typeof(MainTabWindow_History), nameof(MainTabWindow_History.DoGraphPage));
    }

    [HarmonyPriority(MpPriority.MpFirst)]
    static void Prefix(ref TimeSnapshot? __state)
    {
        __state = TimeSnapshot.GetAndSetFromMap(HistoryGraphPerFaction.PlayerMap());
    }

    [HarmonyPriority(MpPriority.MpLast)]
    static void Finalizer(TimeSnapshot? __state) => __state?.Set();
}

[HarmonyPatch(typeof(MainTabWindow_History), nameof(MainTabWindow_History.DoGraphPage))]
static class HistoryGraphOnlyOwnTales
{
    static readonly MethodInfo AllTales =
        AccessTools.PropertyGetter(typeof(TaleManager), nameof(TaleManager.AllTalesListForReading));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        int patched = 0;
        foreach (var inst in insts)
        {
            yield return inst;
            if (inst.Calls(AllTales))
            {
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(HistoryGraphPerFaction), nameof(HistoryGraphPerFaction.TalesForGraph)));
                patched++;
            }
        }

        if (patched != 1)
            Log.Error($"Multiplayer: HistoryGraphOnlyOwnTales patched {patched} tale list reads, expected 1");
    }
}

// Rebuilds the curves shifted onto the map clock; vanilla then sees them as cached and only draws them
[HarmonyPatch(typeof(HistoryAutoRecorderGroup), nameof(HistoryAutoRecorderGroup.DrawGraph))]
static class HistoryGraphCurvesOnMapClock
{
    static HistoryAutoRecorderGroup lastGroup;
    static int lastTicks = -1, lastOffset;

    static void Prefix(HistoryAutoRecorderGroup __instance)
    {
        var map = HistoryGraphPerFaction.PlayerMap();
        if (map == null) return;

        int ticks = Find.TickManager.TicksGame;
        int offset = HistoryGraphPerFaction.CurveOffset(map);
        if (__instance == lastGroup && ticks == lastTicks && offset == lastOffset &&
            __instance.cachedGraphTickCount == ticks)
            return;

        lastGroup = __instance;
        lastTicks = ticks;
        lastOffset = offset;

        __instance.curves.Clear();
        foreach (var recorder in __instance.recorders)
        {
            var info = new SimpleCurveDrawInfo
            {
                color = recorder.def.graphColor,
                label = recorder.def.LabelCap,
                valueFormat = recorder.def.valueFormat,
                curve = new SimpleCurve()
            };

            for (int j = 0; j < recorder.records.Count; j++)
                info.curve.Add(new CurvePoint((j * (float)recorder.def.recordTicksFrequency + offset) / 60000f, recorder.records[j]), sort: false);
            info.curve.SortPoints();

            if (recorder.records.Count == 1)
                info.curve.Add(new CurvePoint(1.6666667E-05f, recorder.records[0]));

            __instance.curves.Add(info);
        }

        __instance.cachedGraphTickCount = ticks;
    }
}
