using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Persistent;

public class PlayerTradeWindow : Window
{
    public readonly PlayerTradeSession session;

    private readonly Faction leftFaction;
    private readonly Faction rightFaction;
    private readonly bool participant;
    private readonly TransferableOneWayWidget leftWidget;
    private readonly TransferableOneWayWidget rightWidget;

    private const float TitleHeight = 40f;
    private const float FooterHeight = 90f;
    private const float Gap = 12f;

    public override Vector2 InitialSize => new(1180f, Mathf.Min(760f, UI.screenHeight));

    public PlayerTradeWindow(PlayerTradeSession session)
    {
        this.session = session;

        doCloseX = true;
        closeOnAccept = false;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        forcePause = false;
        preventCameraMotion = false;
        draggable = true;
        onlyOneOfTypeAllowed = false;

        var me = Multiplayer.RealPlayerFaction;
        participant = session.IsParticipant(me);
        leftFaction = participant ? me : session.initiatorFaction;
        rightFaction = session.OtherSide(leftFaction);

        leftWidget = MakeWidget(leftFaction, readOnly: !participant);
        rightWidget = MakeWidget(rightFaction, readOnly: true);
    }

    private TransferableOneWayWidget MakeWidget(Faction giver, bool readOnly)
    {
        return new TransferableOneWayWidget(
            session.OfferOf(giver),
            giver.Name,
            session.OtherSide(giver).Name,
            "MpPlayerTradeAvailable".Translate(),
            tile: session.Map.Tile,
            drawMarketValue: true)
        {
            readOnly = readOnly
        };
    }

    private bool SessionAlive =>
        session.Map?.MpComp()?.sessionManager.AllSessions.Contains(session) == true;

    public override void DoWindowContents(Rect inRect)
    {
        if (!SessionAlive)
        {
            Close(false);
            return;
        }

        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, TitleHeight),
            "MpPlayerTradeTitle".Translate(session.initiatorFaction.Name, session.otherFaction.Name));
        Text.Font = GameFont.Small;

        float columnWidth = (inRect.width - Gap) / 2f;
        float columnsHeight = inRect.height - TitleHeight - FooterHeight;
        var leftRect = new Rect(inRect.x, inRect.y + TitleHeight, columnWidth, columnsHeight);
        var rightRect = new Rect(leftRect.xMax + Gap, leftRect.y, columnWidth, columnsHeight);

        DrawColumn(leftRect, leftFaction, leftWidget, participant ? "MpPlayerTradeYourOffer".Translate() : null);
        DrawColumn(rightRect, rightFaction, rightWidget, participant ? "MpPlayerTradeTheirOffer".Translate() : null);

        DrawFooter(new Rect(inRect.x, leftRect.yMax + 6f, inRect.width, FooterHeight - 6f));
    }

    private void DrawColumn(Rect rect, Faction giver, TransferableOneWayWidget widget, string heading)
    {
        heading ??= "MpPlayerTradeOfferOf".Translate(giver.Name);

        var accepted = session.AcceptedBy(giver);
        var headingRect = new Rect(rect.x, rect.y, rect.width, 26f);
        Widgets.Label(headingRect, $"{heading} — {"MpPlayerTradeValue".Translate(PlayerTradeSession.OfferValue(session.OfferOf(giver)).ToStringMoney())}");

        GUI.color = accepted ? Color.green : Color.gray;
        Text.Anchor = TextAnchor.UpperRight;
        Widgets.Label(headingRect, accepted ? "MpPlayerTradeAccepted".Translate() : "MpPlayerTradeNotAccepted".Translate());
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;

        var widgetRect = new Rect(rect.x, rect.y + 28f, rect.width, rect.height - 28f);

        SyncSessionWithTransferablesMarker.DrawnSessionWithTransferables = session;
        try
        {
            widget.OnGUI(widgetRect);
        }
        finally
        {
            SyncSessionWithTransferablesMarker.DrawnSessionWithTransferables = null;
        }
    }

    private void DrawFooter(Rect rect)
    {
        Text.Anchor = TextAnchor.MiddleLeft;
        Widgets.Label(new Rect(rect.x, rect.y, rect.width, 28f), "MpPlayerTradeHint".Translate());
        Text.Anchor = TextAnchor.UpperLeft;

        if (!participant)
            return;

        var me = Multiplayer.RealPlayerFaction;
        var buttonSize = new Vector2(200f, 40f);
        float y = rect.yMax - buttonSize.y;
        float centerX = rect.center.x;

        var acceptRect = new Rect(centerX - buttonSize.x / 2f, y, buttonSize.x, buttonSize.y);
        var resetRect = new Rect(acceptRect.x - buttonSize.x - 10f, y, buttonSize.x, buttonSize.y);
        var cancelRect = new Rect(acceptRect.xMax + 10f, y, buttonSize.x, buttonSize.y);

        if (session.AcceptedBy(me))
        {
            if (Widgets.ButtonText(acceptRect, "MpPlayerTradeUnaccept".Translate()))
                session.Unaccept();
        }
        else if (Widgets.ButtonText(acceptRect, "MpPlayerTradeAccept".Translate()))
        {
            session.Accept(session.revision);
        }

        if (Widgets.ButtonText(resetRect, "MpPlayerTradeReset".Translate()))
            session.ResetMyOffer();

        GUI.color = new Color(1f, 0.3f, 0.35f);
        if (Widgets.ButtonText(cancelRect, "MpPlayerTradeCancel".Translate()))
            session.Cancel();
        GUI.color = Color.white;
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
static class PlayerTradeGizmo
{
    static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
    {
        foreach (var gizmo in gizmos)
            yield return gizmo;

        if (Multiplayer.Client == null) yield break;

        var me = Multiplayer.RealPlayerFaction;
        if (me == null || me == Multiplayer.WorldComp.spectatorFaction) yield break;

        var other = __instance.Faction;
        if (other is not { IsPlayer: true } || other == me || other == Multiplayer.WorldComp.spectatorFaction)
            yield break;
        if (!__instance.Spawned || !__instance.RaceProps.Humanlike || !__instance.IsColonist)
            yield break;

        if (PlayerTradeSession.Find(__instance.Map, me, other) is { } existing)
        {
            yield return new Command_Action
            {
                defaultLabel = "MpPlayerTradeOpen".Translate(),
                defaultDesc = "MpPlayerTradeOpenDesc".Translate(other.Name),
                icon = MultiplayerStatic.PlayerTradeIcon,
                action = () => existing.OpenWindow()
            };
            yield break;
        }

        var negotiator = PlayerTradeSession.FindNegotiator(me, __instance);
        var target = __instance;
        var command = new Command_Action
        {
            defaultLabel = "MpPlayerTradeWith".Translate(other.Name),
            defaultDesc = "MpPlayerTradeWithDesc".Translate(other.Name),
            icon = MultiplayerStatic.PlayerTradeIcon,
            action = () => PlayerTradeSession.CreateSession(negotiator, target)
        };

        if (negotiator == null)
            command.Disable("MpPlayerTradeNoNegotiator".Translate());
        else if (__instance.Downed)
            command.Disable("MpPlayerTradeTargetDowned".Translate(__instance.LabelShort));

        yield return command;
    }
}
