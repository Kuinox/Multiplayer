using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Factions;

// Ideos are shared between factions: an obligation created because the ideo is active for one faction
// was shown on the ritual spots of a faction that only has it as a minor ideo (vanilla never creates it there).
[HarmonyPatch(typeof(Precept_Ritual), nameof(Precept_Ritual.GetGizmoFor))]
static class HideInactiveIdeoObligationGizmos
{
    static void Postfix(Precept_Ritual __instance, ref IEnumerable<Gizmo> __result)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return;
        if (__instance.ideo == null || __instance.ideo.ObligationsActive || __instance.def.allowOptionalRitualObligations) return;
        __result = Filter(__result);
    }

    static IEnumerable<Gizmo> Filter(IEnumerable<Gizmo> gizmos)
    {
        foreach (var gizmo in gizmos)
            if (gizmo is not Command_Ritual { obligation: not null })
                yield return gizmo;
    }
}
