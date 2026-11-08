using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.AsyncTime;

// Vanilla caches the grav engine lookup for one TicksGame. With async time TicksGame changes with every map context,
// so the cache was always cleared and each call scanned the whole map (IsPlayerHome on other players maps, every tick).
// Cache it per map on its own clock instead.
[HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.GetPlayerGravEngine_NewTemp))]
static class GravEngineCachePerMapClock
{
    class Entry
    {
        public int mapTicks;
        public Building_GravEngine engine;
    }

    // UI calls are local, they don't share the sim cache
    static readonly ConditionalWeakTable<Map, Entry> simCache = new();
    static readonly ConditionalWeakTable<Map, Entry> uiCache = new();

    static ConditionalWeakTable<Map, Entry> Cache => Multiplayer.Ticking || Multiplayer.ExecutingCmds ? simCache : uiCache;

    static bool Prefix(Map map, ref Building_GravEngine __result)
    {
        if (Multiplayer.Client == null || map?.AsyncTime() is not { } comp) return true;
        if (!Cache.TryGetValue(map, out var entry) || entry.mapTicks != comp.mapTicks) return true;

        __result = entry.engine;
        return false;
    }

    static void Postfix(Map map, Building_GravEngine __result)
    {
        if (Multiplayer.Client == null || map?.AsyncTime() is not { } comp) return;

        var entry = Cache.GetValue(map, _ => new Entry());
        entry.mapTicks = comp.mapTicks;
        entry.engine = __result;
    }
}
