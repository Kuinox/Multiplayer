using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Multiplayer.Client.Patches;

// Construction mechs (e.g. the constructoid) build at a fixed, low skill. This per-mech toggle stops them
// from finishing frames of buildings that have a quality, so a colonist builds those. They still deliver
// materials to such frames.
public static class MechNoQualityBuild
{
    private static Texture2D icon;
    public static Texture2D Icon => icon ??= ContentFinder<Texture2D>.Get("UI/Icons/Rituals/QualityPenalty");

    public static bool IsDisallowed(Pawn pawn) =>
        Multiplayer.Client != null
        && pawn.RaceProps.IsMechanoid
        && Multiplayer.GameComp.mechsNoQualityBuild.Contains(pawn.thingIDNumber);

    public static bool HasQuality(Frame frame) =>
        frame.def.entityDefToBuild is ThingDef def && def.HasComp(typeof(CompQuality));

    public static bool ShowToggleFor(Pawn pawn) =>
        pawn.RaceProps.IsMechanoid
        && pawn.Faction == Faction.OfPlayer
        && pawn.RaceProps.mechEnabledWorkTypes?.Contains(WorkTypeDefOf.Construction) == true;

    [SyncMethod]
    public static void SetDisallowed(Pawn mech, bool disallowed)
    {
        if (mech == null) return;

        var list = Multiplayer.GameComp.mechsNoQualityBuild;
        if (disallowed)
        {
            if (!list.Contains(mech.thingIDNumber))
                list.Add(mech.thingIDNumber);
        }
        else
        {
            list.Remove(mech.thingIDNumber);
        }
    }
}

[HarmonyPatch(typeof(WorkGiver_ConstructFinishFrames), nameof(WorkGiver_ConstructFinishFrames.JobOnThing))]
static class MechNoQualityBuildFinishFramePatch
{
    static bool Prefix(Pawn pawn, Thing t, ref Job __result)
    {
        if (t is not Frame frame || !MechNoQualityBuild.IsDisallowed(pawn) || !MechNoQualityBuild.HasQuality(frame))
            return true;

        JobFailReason.Is("MpMechNoQualityBuildReason".Translate());
        __result = null;
        return false;
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
static class MechNoQualityBuildGizmoPatch
{
    static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
    {
        foreach (var gizmo in gizmos)
            yield return gizmo;

        if (Multiplayer.Client == null || !MechNoQualityBuild.ShowToggleFor(__instance))
            yield break;

        var pawn = __instance;
        yield return new Command_Toggle
        {
            defaultLabel = "MpMechNoQualityBuild".Translate(),
            defaultDesc = "MpMechNoQualityBuildDesc".Translate(),
            icon = MechNoQualityBuild.Icon,
            isActive = () => MechNoQualityBuild.IsDisallowed(pawn),
            toggleAction = () => MechNoQualityBuild.SetDisallowed(pawn, !MechNoQualityBuild.IsDisallowed(pawn))
        };
    }
}
