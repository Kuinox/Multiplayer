using HarmonyLib;
using RimWorld;
using RimWorld.QuestGen;

namespace Multiplayer.Client.Patches;

// RimWorld caches whether a quest script or an incident can run now, per def, valid while TicksGame equals the tick
// it was computed at. These caches are shared by every faction, never saved, and survive loading a save.
// With async time TicksGame is often a paused map's clock (quest choices are made on the faction's home map clock,
// see StorytellerPatches), the same tick forever: a game running for a while answered from a result computed hours
// earlier while one that had just started or joined ran the check and its random rolls, and desynced. Reloading a
// save in a running game reused the results of the abandoned timeline, so the same save gave different quests.
// Never let the simulation reuse them.
static class TickKeyedCaches
{
    public static bool Bypass => Multiplayer.Client != null && !Multiplayer.InInterface;

    [HarmonyPatch(typeof(QuestScriptDef), nameof(QuestScriptDef.CanRun), typeof(Slate), typeof(IIncidentTarget))]
    static class QuestScriptDefCanRun
    {
        private static readonly AccessTools.FieldRef<QuestScriptDef, int> lastCheckTick =
            AccessTools.FieldRefAccess<QuestScriptDef, int>("lastCheckCanRunTick");

        static void Prefix(QuestScriptDef __instance)
        {
            if (Bypass) lastCheckTick(__instance) = int.MinValue;
        }
    }

    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.CanFireNow))]
    static class IncidentWorkerCanFireNow
    {
        private static readonly AccessTools.FieldRef<IncidentWorker, int> lastCheckTick =
            AccessTools.FieldRefAccess<IncidentWorker, int>("lastCheckCanRunTick");

        static void Prefix(IncidentWorker __instance)
        {
            if (Bypass) lastCheckTick(__instance) = int.MinValue;
        }
    }
}
