using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Persistent;

/// <summary>A caravan sent to another player's settlement to trade with it from the world map.</summary>
public class CaravanArrivalAction_MpPlayerTrade : CaravanArrivalAction
{
    private Settlement settlement;

    public override string Label => "MpPlayerTradeWith".Translate(settlement.Faction?.Name ?? settlement.Label);

    public override string ReportString => "MpPlayerTradeCaravanReport".Translate(settlement.Label);

    public CaravanArrivalAction_MpPlayerTrade()
    {
    }

    public CaravanArrivalAction_MpPlayerTrade(Settlement settlement)
    {
        this.settlement = settlement;
    }

    public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
    {
        var report = base.StillValid(caravan, destinationTile);
        if (!report)
            return report;
        if (settlement != null && settlement.Tile != destinationTile)
            return false;
        return PlayerTradeSession.CanTradeFromCaravan(caravan, settlement);
    }

    public override void Arrived(Caravan caravan) => PlayerTradeSession.CreateFromCaravan(caravan, settlement);

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref settlement, "settlement");
    }
}

// Right-clicking another player's settlement with a caravan selected. The world-object caravan menu is synced
// by SyncActions (its postfix runs last), so this must only depend on synced state, never on the local player.
[HarmonyPatch(typeof(Settlement), nameof(Settlement.GetFloatMenuOptions))]
static class PlayerTradeSettlementMenu
{
    static IEnumerable<FloatMenuOption> Postfix(IEnumerable<FloatMenuOption> options, Settlement __instance, Caravan caravan)
    {
        foreach (var option in options)
            yield return option;

        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            yield break;

        var settlement = __instance;
        foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                     () => PlayerTradeSession.CanTradeFromCaravan(caravan, settlement),
                     () => new CaravanArrivalAction_MpPlayerTrade(settlement),
                     "MpPlayerTradeWith".Translate(settlement.Faction?.Name ?? settlement.Label),
                     caravan, settlement.Tile, settlement))
            yield return option;
    }
}

// Reopens the trade window from the caravan, for both the caravan's owner and the host
[HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
static class PlayerTradeCaravanGizmo
{
    static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Caravan __instance)
    {
        foreach (var gizmo in gizmos)
            yield return gizmo;

        if (Multiplayer.Client == null) yield break;

        foreach (var map in Find.Maps)
        {
            var session = map.MpComp().sessionManager.GetFirstOfType<PlayerTradeSession>(s => s.caravan == __instance);
            if (session == null || !session.IsParticipant(Multiplayer.RealPlayerFaction)) continue;

            yield return new Command_Action
            {
                defaultLabel = "MpPlayerTradeOpen".Translate(),
                defaultDesc = "MpPlayerTradeOpenDesc".Translate(session.OtherSide(Multiplayer.RealPlayerFaction).Name),
                icon = MultiplayerStatic.PlayerTradeIcon,
                action = () => session.OpenWindow()
            };
            yield break;
        }
    }
}
