using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.Client.AsyncTime;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Factions;

// Vanilla queues each pollution event (a wastepack dissolving outside the player's home, a temporary map closing
// more polluted than it was generated) as a tile and an amount, then once per world tick angers the nearest
// settlement with Faction.OfPlayer and may send it to retaliate on "a player map". In multifaction the world tick
// runs as the spectator faction: the spectator lost the goodwill, another player's colony could be the angered
// settlement, and the retaliation raided whichever player's map came up.
// Remember who polluted for every event and handle each polluter's events as that faction.
static class PollutionByFaction
{
    private static readonly IList PendingEvents =
        (IList)AccessTools.Field(typeof(CompDissolutionEffect_Goodwill), "pendingGoodwillEvents").GetValue(null);

    // Parallel to CompDissolutionEffect_Goodwill.pendingGoodwillEvents, null when no player is to blame
    private static readonly List<Faction> polluters = new();

    private static Thing dissolving;
    private static bool handlingFaction;

    private static bool Active => Multiplayer.Client != null && Multiplayer.GameComp.multifaction;

    private static Faction RealPlayer(Faction faction) =>
        faction is { IsPlayer: true } && faction != Multiplayer.WorldComp.spectatorFaction ? faction : null;

    private static Faction FindPolluter(Thing thing)
    {
        // A player's own map (its context is the player) or a command a player sent
        if (RealPlayer(Faction.OfPlayer) is { } current) return current;

        // Carried in a caravan, a pawn's inventory or transport pods
        for (IThingHolder holder = thing?.ParentHolder; holder != null; holder = holder.ParentHolder)
        {
            var owner = holder switch
            {
                Thing t => RealPlayer(t.Faction),
                WorldObject wo => RealPlayer(wo.Faction),
                _ => null
            };
            if (owner != null) return owner;
        }

        // Dissolving on a map no player owns (a dumping site): the player with the most colonists there
        var map = thing?.MapHeld;
        if (map == null) return null;

        return map.mapPawns.AllPawnsSpawned
            .Where(p => p.RaceProps.Humanlike && RealPlayer(p.Faction) != null)
            .GroupBy(p => p.Faction)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.loadID)
            .FirstOrDefault()?.Key;
    }

    private static void Record(int countBefore, Thing thing)
    {
        if (!Active) return;

        // Keep the lists aligned, they are only ever appended to and cleared together
        while (polluters.Count > countBefore) polluters.RemoveAt(polluters.Count - 1);
        while (polluters.Count < countBefore) polluters.Add(null);
        for (int i = countBefore; i < PendingEvents.Count; i++)
            polluters.Add(FindPolluter(thing));
    }

    // Both lists are static and not saved: events still pending when a game was left would be handled in the next
    // game loaded (e.g. a rollback reloading a save), on that client only
    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    static class ClearOnLoad
    {
        static void Postfix()
        {
            PendingEvents.Clear();
            polluters.Clear();
        }
    }

    [HarmonyPatch(typeof(CompDissolutionEffect_Goodwill), nameof(CompDissolutionEffect_Goodwill.DoDissolutionEffectMap))]
    static class RecordMapEvent
    {
        static void Prefix(out int __state) => __state = PendingEvents.Count;
        static void Postfix(CompDissolutionEffect_Goodwill __instance, int __state) => Record(__state, __instance.parent);
    }

    // Covers DoDissolutionEffectWorld (a wastepack dissolving off any map) and PollutionInfo.MapRemoved
    [HarmonyPatch(typeof(CompDissolutionEffect_Goodwill), nameof(CompDissolutionEffect_Goodwill.AddWorldDissolutionEvent))]
    static class RecordWorldEvent
    {
        static void Prefix(out int __state) => __state = PendingEvents.Count;
        static void Postfix(int __state) => Record(__state, dissolving);
    }

    [HarmonyPatch(typeof(CompDissolutionEffect_Goodwill), nameof(CompDissolutionEffect_Goodwill.DoDissolutionEffectWorld))]
    static class RememberDissolvingThing
    {
        static void Prefix(CompDissolutionEffect_Goodwill __instance) => dissolving = __instance.parent;
        static void Finalizer() => dissolving = null;
    }

    [HarmonyPatch(typeof(CompDissolutionEffect_Goodwill), nameof(CompDissolutionEffect_Goodwill.WorldUpdate))]
    static class HandleEventsPerPolluter
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix()
        {
            if (!Active || handlingFaction) return true;
            // Like UpdateWorldStateWhenTickingOnly: vanilla also calls it from the world's update, only the world tick may handle them
            if (!AsyncWorldTimeComp.tickingWorld || PendingEvents.Count == 0) return false;

            if (polluters.Count != PendingEvents.Count)
                Log.Error($"[Multiplayer] Pollution events ({PendingEvents.Count}) and their polluters ({polluters.Count}) are out of sync");

            var events = PendingEvents.Cast<object>().ToList();
            var byPolluter = events
                .Select((e, i) => (e, polluter: i < polluters.Count ? polluters[i] : null))
                .GroupBy(x => x.polluter)
                .OrderBy(g => g.Key?.loadID ?? int.MinValue)
                .ToList();

            try
            {
                foreach (var group in byPolluter)
                {
                    if (group.Key == null) continue; // no player to blame

                    PendingEvents.Clear();
                    foreach (var (e, _) in group) PendingEvents.Add(e);

                    FactionExtensions.PushFaction(null, group.Key, force: true);
                    handlingFaction = true;
                    try
                    {
                        CompDissolutionEffect_Goodwill.WorldUpdate();
                    }
                    finally
                    {
                        handlingFaction = false;
                        FactionExtensions.PopFaction();
                    }
                }
            }
            finally
            {
                PendingEvents.Clear();
                polluters.Clear();
            }

            return false;
        }
    }

    // The angered settlement skipped only Faction.OfPlayer's: never pick another player's colony or the spectator's
    [HarmonyPatch(typeof(CompDissolutionEffect_Goodwill), "TryGetAffectedSettlement")]
    static class NeverAngerPlayers
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts, MethodBase original)
        {
            var ofPlayer = AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer));
            var worldObjectFaction = AccessTools.PropertyGetter(typeof(WorldObject), nameof(WorldObject.Faction));
            int replaced = 0;
            CodeInstruction prev = null;

            foreach (var inst in insts)
            {
                // item.Faction != Faction.OfPlayer  ->  item.Faction != ExcludedAs(item.Faction)
                if (inst.Calls(ofPlayer) && prev != null && prev.Calls(worldObjectFaction))
                {
                    yield return new CodeInstruction(OpCodes.Dup).MoveLabelsFrom(inst);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NeverAngerPlayers), nameof(ExcludedAs)));
                    replaced++;
                    prev = inst;
                    continue;
                }

                prev = inst;
                yield return inst;
            }

            if (replaced == 0)
                Log.Error($"Multiplayer: {original} no longer compares settlement factions with Faction.OfPlayer");
        }

        // Returns the faction itself when its settlements must be skipped, Faction.OfPlayer otherwise
        static Faction ExcludedAs(Faction settlementFaction)
        {
            if (Active && (settlementFaction is { IsPlayer: true } || settlementFaction == Multiplayer.WorldComp.spectatorFaction))
                return settlementFaction;
            return Faction.OfPlayer;
        }
    }
}
