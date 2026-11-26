using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Client
{
    public class TransporterLoading : ExposableSession, ISessionWithTransferables
    {
        public override Map Map => map;

        public Map map;

        public Faction faction;

        public List<CompTransporter> transporters;
        public List<ThingWithComps> pods;
        public List<TransferableOneWay> transferables;

        public bool uiDirty;

        public override bool IsSessionValid =>
            map != null && faction != null &&
            transporters is { Count: > 0 } && transporters.All(t => t?.parent != null);

        // Used when saving and loading
        public TransporterLoading(Map map) : base(map)
        {
            this.map = map;
        }

        public TransporterLoading(Faction faction, Map map) : base(map)
        {
            this.map = map;
            this.faction = faction;
        }

        public TransporterLoading(Faction faction, Map map, List<CompTransporter> transporters) : this(faction, map)
        {
            this.transporters = transporters;
            pods = transporters.Select(t => t.parent).ToList();

            AddItems();
        }

        private void AddItems()
        {
            var dialog = new TransporterLoadingProxy(map, transporters);

            // Init code taken from Dialog_LoadTransporters.PostOpen
            dialog.CalculateAndRecacheTransferables();
            if (dialog.CanChangeAssignedThingsAfterStarting && dialog.LoadingInProgressOrReadyToLaunch)
                dialog.SetLoadedItemsToLoad();

            transferables = dialog.transferables;
        }

        [SyncMethod]
        public void TryAccept()
        {
            if (PrepareDummyDialog().TryAccept())
                Remove();
        }

        [SyncMethod(debugOnly = true)]
        public void DebugTryLoadInstantly()
        {
            if (PrepareDummyDialog().DebugTryLoadInstantly())
                Remove();
        }

        [SyncMethod]
        public void Reset()
        {
            transferables.ForEach(t => t.CountToTransfer = 0);
            uiDirty = true;
        }

        [SyncMethod]
        public void Remove()
        {
            map.MpComp().sessionManager.RemoveSession(this);
        }

        public void OpenWindow(bool sound = true)
        {
            var dialog = PrepareDummyDialog();
            if (!sound)
                dialog.soundAppear = null;

            Find.WindowStack.Add(dialog);
        }

        private TransporterLoadingProxy PrepareDummyDialog()
        {
            return new TransporterLoadingProxy(map, transporters)
            {
                itemsReady = true,
                transferables = transferables
            };
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_References.Look(ref faction, "faction");
            Scribe_Collections.Look(ref transferables, "transferables", LookMode.Deep);
            Scribe_Collections.Look(ref pods, "transporters", LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.ResolvingCrossRefs)
            {
                // A pod that was destroyed while the session was open fails to resolve and comes back null.
                // IsSessionValid then drops the session when the sessions finish loading.
                pods ??= new List<ThingWithComps>();
                transporters = pods.Select(t => t?.GetComp<CompTransporter>()).ToList();
            }
        }

        public Transferable GetTransferableByThingId(int thingId)
        {
            return transferables.Find(tr => tr.things.Any(t => t.thingIDNumber == thingId));
        }

        public void Notify_CountChanged(Transferable tr)
        {
            uiDirty = true;
        }

        public override bool IsCurrentlyPausing(Map map) => map == this.map;

        public bool Contains(CompTransporter transporter) => transporters != null && transporters.Contains(transporter);

        public override FloatMenuOption GetBlockingWindowOptions(ColonistBar.Entry entry)
        {
            return new FloatMenuOption("MpTransportLoadingSession".Translate(), () =>
            {
                SwitchToMapOrWorld(entry.map);
                OpenWindow();
            });
        }
    }

    // The vanilla loading dialog is modal and pauses the game, so its transporters can't go anywhere while it's open.
    // In multiplayer the session outlives the window: the pod can still leave while it is open (a quest shuttle
    // departing on the world clock, a launch or a deconstruct from a command...). The session then kept pointing at
    // a despawned transporter: the window threw every frame, and after a reload the null pod broke the session.
    [HarmonyPatch(typeof(CompTransporter), nameof(CompTransporter.PostDeSpawn))]
    static class EndTransporterLoadingWhenTransporterLeaves
    {
        static void Postfix(CompTransporter __instance, Map map)
        {
            if (Multiplayer.Client == null || map == null) return;

            var manager = map.MpComp()?.sessionManager;
            if (manager == null) return;

            TransporterLoading session;
            while ((session = manager.GetFirstOfType<TransporterLoading>(s => s.Contains(__instance))) != null)
                manager.RemoveSession(session);
        }
    }
}
