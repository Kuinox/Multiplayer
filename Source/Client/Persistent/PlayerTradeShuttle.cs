using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Persistent;

public class TransportersArrivalAction_MpPlayerTrade : TransportersArrivalAction_FormCaravan
{
    private Settlement settlement;

    public TransportersArrivalAction_MpPlayerTrade()
    {
    }

    public TransportersArrivalAction_MpPlayerTrade(Settlement settlement) : base("MessageShuttleArrived")
    {
        this.settlement = settlement;
    }

    public override void Arrived(List<ActiveTransporterInfo> transporters, PlanetTile tile)
    {
        var shuttle = transporters.FirstOrDefault()?.GetShuttle();

        // Form the shuttle caravan before checking trade eligibility again, so a trade
        // that becomes unavailable in flight does not cause a landing on the settlement map.
        base.Arrived(transporters, tile);

        if (settlement != null && shuttle?.GetCaravan() is { } caravan && caravan.Tile == settlement.Tile)
            PlayerTradeSession.CreateFromCaravan(caravan, settlement);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref settlement, "settlement");
    }
}

[HarmonyPatch(typeof(Settlement), nameof(Settlement.GetShuttleFloatMenuOptions))]
static class PlayerTradeShuttleMenu
{
    static IEnumerable<FloatMenuOption> Postfix(IEnumerable<FloatMenuOption> options, Settlement __instance,
        IEnumerable<IThingHolder> pods, Action<PlanetTile, TransportersArrivalAction> launchAction)
    {
        foreach (var option in options)
            yield return option;

        var settlement = __instance;
        foreach (var option in TransportersArrivalActionUtility.GetFloatMenuOptions(
                     () => CanTradeFromShuttle(pods, settlement),
                     () => new TransportersArrivalAction_MpPlayerTrade(settlement),
                     "MpPlayerTradeWith".Translate(settlement.Faction?.Name ?? settlement.Label),
                     launchAction, settlement.Tile))
            yield return option;
    }

    private static FloatMenuAcceptanceReport CanTradeFromShuttle(IEnumerable<IThingHolder> pods, Settlement settlement)
    {
        if (pods.FirstOrDefault() is not CompTransporter { parent: Building_PassengerShuttle } shuttle)
            return false;

        var visitor = Faction.OfPlayer;
        if (shuttle.parent.Faction != visitor)
            return false;

        var report = PlayerTradeSession.CanTradeAtSettlement(visitor, settlement);
        if (!report)
            return report;

        if (!TransportersArrivalAction_FormCaravan.CanFormCaravanAt(pods, settlement.Tile))
            return false;

        // A shuttle on the world map carries its passengers in the caravan, not its transporter.
        if (shuttle.parent.GetCaravan() is { } caravan)
            return PlayerTradeSession.CanTradeFromCaravan(caravan, settlement);

        var passengers = pods.SelectMany(pod => pod.GetDirectlyHeldThings()).OfType<Pawn>();
        if (passengers.Any(pawn => CaravanUtility.IsOwner(pawn, visitor) &&
                                   !pawn.Dead && !pawn.Downed && !pawn.InMentalState &&
                                   !StatDefOf.TradePriceImprovement.Worker.IsDisabledFor(pawn) &&
                                   pawn.CanTradeWith(settlement.Faction, null).Accepted))
            return true;

        return FloatMenuAcceptanceReport.WithFailReason("MpPlayerTradeNoNegotiator".Translate());
    }
}
