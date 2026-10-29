using HarmonyLib;
using Multiplayer.Client.Factions;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Patches;

// The world normally ticks only the currently installed stack. Faction-owned stacks
// must all advance their delayed letters, with the matching archive installed.
[HarmonyPatch(typeof(LetterStack), nameof(LetterStack.LetterStackTick))]
static class TickFactionLetterStacks
{
    private static bool ticking;

    static bool Prefix()
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction || ticking)
            return true;

        ticking = true;
        try
        {
            foreach (var (factionId, data) in Multiplayer.WorldComp.factionData)
            {
                var faction = Find.FactionManager.GetById(factionId);
                if (faction == null) continue;

                FactionExtensions.PushFaction(null, faction);
                try
                {
                    data.letterStack.LetterStackTick();
                }
                finally
                {
                    FactionExtensions.PopFaction();
                }
            }
        }
        finally
        {
            ticking = false;
        }

        return false;
    }
}
