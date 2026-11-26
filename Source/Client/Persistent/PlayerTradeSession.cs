using System.Collections.Generic;
using System.Linq;
using Multiplayer.API;
using Multiplayer.Client.Factions;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Persistent;

/// <summary>
/// A trade between two player factions. Either both stand on the same map (map mode), or the initiator is a
/// caravan parked on the other faction's settlement tile (caravan mode: the session lives on that settlement's
/// map and the caravan offers its inventory). Each side offers items from its own storage/home area or from its
/// pawns' inventories, both sides accept, and the items swap. The session never pauses the map: the offer is
/// re-validated when the trade executes.
/// </summary>
public class PlayerTradeSession : ExposableSession, ISessionWithTransferables, ISessionWithCreationRestrictions, ITickingSession
{
    private Map map;

    public Faction initiatorFaction;
    public Faction otherFaction;
    public Pawn initiatorNegotiator;
    public Pawn otherNegotiator; // null in caravan mode: the host trades from its settlement

    // Caravan mode: the initiator's caravan visiting the other faction's settlement on this map's tile
    public Caravan caravan;

    // What each faction gives away. CountToTransfer is the offered amount.
    public List<TransferableOneWay> initiatorOffer = new();
    public List<TransferableOneWay> otherOffer = new();

    public bool initiatorAccepted;
    public bool otherAccepted;

    // Bumped on every change to either offer. An accept only counts against the revision the player saw.
    public int revision;

    public override Map Map => map;

    public override bool IsSessionValid =>
        map != null && initiatorFaction != null && otherFaction != null &&
        initiatorNegotiator != null && (caravan != null || otherNegotiator != null);

    public PlayerTradeSession(Map map) : base(map)
    {
        this.map = map;
    }

    public bool IsParticipant(Faction f) => f != null && (f == initiatorFaction || f == otherFaction);

    public Faction OtherSide(Faction f) => f == initiatorFaction ? otherFaction : initiatorFaction;

    public List<TransferableOneWay> OfferOf(Faction f) => f == initiatorFaction ? initiatorOffer : otherOffer;

    public Pawn NegotiatorOf(Faction f) => f == initiatorFaction ? initiatorNegotiator : otherNegotiator;

    public bool AcceptedBy(Faction f) => f == initiatorFaction ? initiatorAccepted : otherAccepted;

    public static float OfferValue(List<TransferableOneWay> offer)
    {
        float value = 0;
        foreach (var tr in offer)
            if (tr.CountToTransfer > 0 && tr.AnyThing is { } t)
                value += t.MarketValue * tr.CountToTransfer;
        return value;
    }

    public static PlayerTradeSession Find(Map map, Faction a, Faction b) =>
        map?.MpComp().sessionManager.GetFirstOfType<PlayerTradeSession>(s =>
            s.IsParticipant(a) && s.IsParticipant(b));

    /// <summary>The negotiator for a faction: its free colonist closest to the target pawn.</summary>
    public static Pawn FindNegotiator(Faction faction, Pawn target) =>
        target.Map.mapPawns.SpawnedPawnsInFaction(faction)
            .Where(p => p.IsColonist && !p.Downed && !p.IsPrisoner && p.RaceProps.Humanlike)
            .OrderBy(p => p.Position.DistanceToSquared(target.Position))
            .ThenBy(p => p.thingIDNumber)
            .FirstOrDefault();

    public static bool ValidNegotiator(Pawn p, Faction faction, Map map) =>
        p is { Destroyed: false, Dead: false, Spawned: true } && p.Map == map && p.Faction == faction;

    [SyncMethod]
    public static void CreateSession(Pawn initiator, Pawn other)
    {
        var map = initiator?.Map;
        if (map == null || other?.Map != map) return;

        var initiatorFaction = initiator.Faction;
        var otherFaction = other.Faction;
        if (initiatorFaction is not { IsPlayer: true } || otherFaction is not { IsPlayer: true } ||
            initiatorFaction == otherFaction)
            return;

        // Only the initiator's own player may start a trade on its behalf
        if (Faction.OfPlayer != initiatorFaction) return;

        if (Find(map, initiatorFaction, otherFaction) is { } existing)
        {
            if (MP.IsExecutingSyncCommandIssuedBySelf)
                existing.OpenWindow();
            return;
        }

        var session = new PlayerTradeSession(map)
        {
            initiatorFaction = initiatorFaction,
            otherFaction = otherFaction,
            initiatorNegotiator = initiator,
            otherNegotiator = other,
        };
        session.initiatorOffer = CollectOffer(map, initiatorFaction);
        session.otherOffer = CollectOffer(map, otherFaction);

        if (!map.MpComp().sessionManager.AddSession(session))
            return;

        if (MP.IsExecutingSyncCommandIssuedBySelf)
        {
            session.OpenWindow();
        }
        else if (Multiplayer.RealPlayerFaction == otherFaction)
        {
            Messages.Message("MpPlayerTradeRequested".Translate(initiatorFaction.Name), initiator,
                MessageTypeDefOf.NeutralEvent, false);
            session.OpenWindow();
        }
    }

    public static FloatMenuAcceptanceReport CanTradeFromCaravan(Caravan caravan, Settlement settlement)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction || caravan == null || settlement == null)
            return false;

        var host = settlement.Faction;
        var visitor = caravan.Faction;
        var spectator = Multiplayer.WorldComp.spectatorFaction;
        if (host is not { IsPlayer: true } || visitor is not { IsPlayer: true } || host == visitor ||
            host == spectator || visitor == spectator)
            return false;

        if (!settlement.Spawned || !settlement.HasMap)
            return FloatMenuAcceptanceReport.WithFailReason("MpPlayerTradeNoMap".Translate());

        if (BestCaravanPawnUtility.FindBestNegotiator(caravan, host) == null)
            return FloatMenuAcceptanceReport.WithFailReason("MpPlayerTradeNoNegotiator".Translate());

        return true;
    }

    /// <summary>Runs on every client when a caravan sent to trade arrives (world tick).</summary>
    public static void CreateFromCaravan(Caravan caravan, Settlement settlement)
    {
        if (!CanTradeFromCaravan(caravan, settlement)) return;

        var map = settlement.Map;
        var visitor = caravan.Faction;
        var host = settlement.Faction;

        if (Find(map, visitor, host) is { } existing)
        {
            if (existing.IsParticipant(Multiplayer.RealPlayerFaction))
                existing.OpenWindow();
            return;
        }

        var session = new PlayerTradeSession(map)
        {
            initiatorFaction = visitor,
            otherFaction = host,
            initiatorNegotiator = BestCaravanPawnUtility.FindBestNegotiator(caravan, host),
            caravan = caravan,
        };
        session.initiatorOffer = CollectCaravanOffer(caravan);
        session.otherOffer = CollectOffer(map, host);

        if (!map.MpComp().sessionManager.AddSession(session))
            return;

        if (Multiplayer.RealPlayerFaction == host)
            Messages.Message("MpPlayerTradeRequestedCaravan".Translate(visitor.Name), caravan,
                MessageTypeDefOf.NeutralEvent, false);
        if (session.IsParticipant(Multiplayer.RealPlayerFaction))
            session.OpenWindow();
    }

    private static List<TransferableOneWay> CollectCaravanOffer(Caravan caravan)
    {
        var result = new List<TransferableOneWay>();
        foreach (var t in CaravanInventoryUtility.AllInventoryItems(caravan))
            if (Offerable(t))
                AddToOffer(result, t);
        return result;
    }

    private static void AddToOffer(List<TransferableOneWay> offer, Thing t)
    {
        var tr = TransferableUtility.TransferableMatching(t, offer, TransferAsOneMode.Normal);
        if (tr == null)
        {
            tr = new TransferableOneWay();
            offer.Add(tr);
        }
        tr.things.Add(t);
    }

    /// <summary>
    /// Items a faction can offer on a map: things in its home area or its storage (both are per-faction
    /// in multifaction, so the faction's map data is pushed), and its pawns' inventories.
    /// Must run in a synced context; the list order is deterministic.
    /// </summary>
    private static List<TransferableOneWay> CollectOffer(Map map, Faction faction)
    {
        var result = new List<TransferableOneWay>();
        void Add(Thing t) => AddToOffer(result, t);

        map.PushFaction(faction, force: true);
        try
        {
            foreach (var t in map.listerThings.AllThings)
            {
                if (!Offerable(t) || !t.Spawned || t.Position.Fogged(map))
                    continue;
                if (map.areaManager.Home[t.Position] || t.IsInAnyStorage())
                    Add(t);
            }
        }
        finally
        {
            map.PopFaction();
        }

        foreach (var pawn in map.mapPawns.SpawnedPawnsInFaction(faction))
        {
            if (pawn.inventory?.innerContainer is not { } inv || pawn.IsPrisoner)
                continue;
            foreach (var t in inv)
                if (Offerable(t))
                    Add(t);
        }

        return result;
    }

    private static bool Offerable(Thing t) =>
        t.def.category == ThingCategory.Item && t is not Corpse && !t.Destroyed;

    /// <summary>Whether the faction still holds the thing on this map, as when the offer was collected.</summary>
    private bool StillHeldBy(Thing t, Faction faction)
    {
        if (t.Destroyed) return false;
        if (t.Spawned) return t.Map == map && !(caravan != null && faction == initiatorFaction);
        if (t.ParentHolder is not Pawn_InventoryTracker { pawn: { } holder } || holder.Faction != faction)
            return false;
        if (caravan != null && faction == initiatorFaction)
            return holder.GetCaravan() == caravan;
        return holder.Spawned && holder.Map == map;
    }

    private bool CaravanStillVisiting() =>
        caravan is { Destroyed: false, Spawned: true } && !caravan.pather.Moving &&
        caravan.Tile == map.Tile && caravan.Faction == initiatorFaction;

    public bool ShouldCancel(out string reason)
    {
        reason = null;
        if (!initiatorFaction.IsPlayer || !otherFaction.IsPlayer)
        {
            reason = "MpPlayerTradeCancelledFaction".Translate();
            return true;
        }

        if (caravan != null)
        {
            if (!CaravanStillVisiting())
            {
                reason = "MpPlayerTradeCancelledCaravanLeft".Translate(initiatorFaction.Name);
                return true;
            }

            if (initiatorNegotiator is not { Dead: false } || initiatorNegotiator.GetCaravan() != caravan)
            {
                reason = "MpPlayerTradeCancelledNegotiator".Translate(initiatorNegotiator?.LabelShort ?? "?", initiatorFaction.Name);
                return true;
            }

            if (map.Parent?.Faction != otherFaction)
            {
                reason = "MpPlayerTradeCancelledFaction".Translate();
                return true;
            }

            return false;
        }

        foreach (var f in new[] { initiatorFaction, otherFaction })
        {
            var negotiator = NegotiatorOf(f);
            if (!ValidNegotiator(negotiator, f, map))
            {
                reason = "MpPlayerTradeCancelledNegotiator".Translate(negotiator?.LabelShort ?? "?", f.Name);
                return true;
            }
        }

        return false;
    }

    public void Tick()
    {
        if (!map.IsHashIntervalTick(60)) return;
        if (ShouldCancel(out var reason))
            End(reason);
    }

    private void End(string reason)
    {
        map.MpComp().sessionManager.RemoveSession(this);
        if (reason != null && IsParticipant(Multiplayer.RealPlayerFaction))
            Messages.Message(reason, MessageTypeDefOf.NeutralEvent, false);
    }

    private void OfferChanged()
    {
        revision++;
        initiatorAccepted = false;
        otherAccepted = false;
    }

    [SyncMethod]
    public void Accept(int seenRevision)
    {
        var me = Faction.OfPlayer;
        if (!IsParticipant(me)) return;

        if (seenRevision != revision)
        {
            if (MP.IsExecutingSyncCommandIssuedBySelf)
                Messages.Message("MpPlayerTradeOfferChanged".Translate(), MessageTypeDefOf.RejectInput, false);
            return;
        }

        if (me == initiatorFaction) initiatorAccepted = true;
        else otherAccepted = true;

        if (initiatorAccepted && otherAccepted)
            TryExecute();
    }

    [SyncMethod]
    public void Unaccept()
    {
        var me = Faction.OfPlayer;
        if (me == initiatorFaction) initiatorAccepted = false;
        else if (me == otherFaction) otherAccepted = false;
    }

    [SyncMethod]
    public void ResetMyOffer()
    {
        var me = Faction.OfPlayer;
        if (!IsParticipant(me)) return;
        OfferOf(me).ForEach(t => t.CountToTransfer = 0);
        OfferChanged();
    }

    [SyncMethod]
    public void Cancel()
    {
        var me = Faction.OfPlayer;
        if (!IsParticipant(me)) return;
        End("MpPlayerTradeCancelledBy".Translate(me.Name));
    }

    private void TryExecute()
    {
        if (ShouldCancel(out var reason))
        {
            End(reason);
            return;
        }

        // One side may offer nothing (a gift), but not both
        if (!initiatorOffer.Any(t => t.CountToTransfer > 0) && !otherOffer.Any(t => t.CountToTransfer > 0))
        {
            initiatorAccepted = false;
            otherAccepted = false;
            if (IsParticipant(Multiplayer.RealPlayerFaction))
                Messages.Message("MpPlayerTradeEmpty".Translate(), MessageTypeDefOf.RejectInput, false);
            return;
        }

        // Every offered amount must still be held, or nothing is traded
        foreach (var f in new[] { initiatorFaction, otherFaction })
        {
            foreach (var tr in OfferOf(f))
            {
                if (tr.CountToTransfer <= 0) continue;
                var available = tr.things.Where(t => StillHeldBy(t, f)).Sum(t => t.stackCount);
                if (available < tr.CountToTransfer)
                {
                    OfferChanged();
                    if (IsParticipant(Multiplayer.RealPlayerFaction))
                        Messages.Message("MpPlayerTradeMissingItems".Translate(f.Name, tr.Label), MessageTypeDefOf.RejectInput, false);
                    return;
                }
            }
        }

        // Take everything out first, then hand it over, so a side never receives its own items back.
        var toInitiator = TakeOffer(otherFaction);
        var toOther = TakeOffer(initiatorFaction);

        Deliver(toInitiator, initiatorFaction);
        Deliver(toOther, otherFaction);

        map.MpComp().sessionManager.RemoveSession(this);
        if (IsParticipant(Multiplayer.RealPlayerFaction))
            Messages.Message("MpPlayerTradeCompleted".Translate(initiatorFaction.Name, otherFaction.Name),
                MessageTypeDefOf.PositiveEvent, false);
    }

    private List<Thing> TakeOffer(Faction giver)
    {
        var taken = new List<Thing>();

        map.PushFaction(giver, force: true);
        try
        {
            foreach (var tr in OfferOf(giver))
            {
                if (tr.CountToTransfer <= 0) continue;
                var held = tr.things.Where(t => StillHeldBy(t, giver)).ToList();
                TransferableUtility.Transfer(held, tr.CountToTransfer, (t, _) =>
                {
                    // Transfer returns the original thing when it takes the whole stack
                    if (t.Spawned)
                        t.DeSpawn();
                    else
                        t.holdingOwner?.Remove(t);
                    taken.Add(t);
                });
            }
        }
        finally
        {
            map.PopFaction();
        }

        return taken;
    }

    private void Deliver(List<Thing> things, Faction receiver)
    {
        if (caravan != null && receiver == initiatorFaction)
        {
            foreach (var t in things)
                CaravanInventoryUtility.GiveThing(caravan, t);
            return;
        }

        // Spawning under the receiver's context makes the items unforbidden for the receiver
        map.PushFaction(receiver, force: true);
        try
        {
            // Away from home (e.g. visiting by caravan) the receiver leaves with only what its pawns carry,
            // so items go into its pawns' inventories, like vanilla caravan trading. Overweight is allowed.
            var carriers = map.IsPlayerHome
                ? new List<Pawn>()
                : map.mapPawns.SpawnedPawnsInFaction(receiver)
                    .Where(p => p.inventory != null && !p.Dead && !p.IsPrisoner &&
                                (p.IsColonist || p.RaceProps.packAnimal) && MassUtility.Capacity(p) > 0f)
                    .ToList();

            // Without a negotiator (the host in caravan mode) items arrive at the trade drop spot,
            // near an orbital trade beacon or comms console, like vanilla orbital trade
            var dropCell = NegotiatorOf(receiver)?.Position ?? DropCellFinder.TradeDropSpot(map);

            foreach (var t in things)
            {
                if (carriers.Count > 0)
                {
                    var carrier = carriers
                        .OrderByDescending(MassUtility.FreeSpace)
                        .ThenBy(p => p.thingIDNumber)
                        .First();
                    if (carrier.inventory.innerContainer.TryAdd(t))
                        continue;
                }

                if (!GenPlace.TryPlaceThing(t, dropCell, map, ThingPlaceMode.Near))
                    GenSpawn.Spawn(t, dropCell, map);
                if (t.Spawned)
                    t.SetForbidden(false, false);
            }
        }
        finally
        {
            map.PopFaction();
        }
    }

    public void OpenWindow(bool sound = true)
    {
        var existing = Verse.Find.WindowStack.Windows.OfType<PlayerTradeWindow>().FirstOrDefault(w => w.session == this);
        if (existing != null)
        {
            Verse.Find.WindowStack.Notify_ClickedInsideWindow(existing);
            return;
        }

        var window = new PlayerTradeWindow(this);
        if (!sound)
            window.soundAppear = null;
        Verse.Find.WindowStack.Add(window);
    }

    public override bool IsCurrentlyPausing(Map map) => false;

    public override FloatMenuOption GetBlockingWindowOptions(ColonistBar.Entry entry)
    {
        if (entry.map != map || !IsParticipant(Multiplayer.RealPlayerFaction))
            return null;

        return new FloatMenuOption("MpPlayerTradeSession".Translate(OtherSide(Multiplayer.RealPlayerFaction).Name), () =>
        {
            SwitchToMapOrWorld(map);
            OpenWindow();
        });
    }

    public override void PostRemoveSession()
    {
        foreach (var w in Verse.Find.WindowStack?.Windows.OfType<PlayerTradeWindow>().Where(w => w.session == this).ToList() ?? [])
            w.Close(doCloseSound: false);
    }

    public Transferable GetTransferableByThingId(int thingId) =>
        initiatorOffer.Find(tr => tr.things.Any(t => t.thingIDNumber == thingId)) ??
        otherOffer.Find(tr => tr.things.Any(t => t.thingIDNumber == thingId));

    public void Notify_CountChanged(Transferable tr) => OfferChanged();

    public bool CanExistWith(Session other) =>
        other is not PlayerTradeSession s || !(s.IsParticipant(initiatorFaction) && s.IsParticipant(otherFaction));

    public override void ExposeData()
    {
        base.ExposeData();

        Scribe_References.Look(ref initiatorFaction, "initiatorFaction");
        Scribe_References.Look(ref otherFaction, "otherFaction");
        Scribe_References.Look(ref initiatorNegotiator, "initiatorNegotiator");
        Scribe_References.Look(ref otherNegotiator, "otherNegotiator");
        Scribe_References.Look(ref caravan, "caravan");
        Scribe_Collections.Look(ref initiatorOffer, "initiatorOffer", LookMode.Deep);
        Scribe_Collections.Look(ref otherOffer, "otherOffer", LookMode.Deep);
        Scribe_Values.Look(ref initiatorAccepted, "initiatorAccepted");
        Scribe_Values.Look(ref otherAccepted, "otherAccepted");
        Scribe_Values.Look(ref revision, "revision");

        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            initiatorOffer ??= new();
            otherOffer ??= new();
            initiatorOffer.RemoveAll(tr => !tr.HasAnyThing);
            otherOffer.RemoveAll(tr => !tr.HasAnyThing);
        }
    }
}
