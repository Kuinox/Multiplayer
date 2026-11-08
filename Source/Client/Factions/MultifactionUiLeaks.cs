using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace Multiplayer.Client.Factions;

// In multifaction, messages and alerts about other players' pawns and maps were shown to everyone.
static class MultifactionUiLeaks
{
    public static bool Active => Multiplayer.Client != null && Multiplayer.GameComp.multifaction;

    public static Faction OwnerFaction(Thing t)
    {
        if (t is Pawn p)
        {
            if (p.HostFaction is { IsPlayer: true }) return p.HostFaction;
            if (p.Faction is { IsPlayer: true }) return p.Faction;
        }
        else if (t.Faction is { IsPlayer: true })
        {
            return t.Faction;
        }

        return t.MapHeld?.ParentFaction;
    }

    public static Faction OwnerFaction(GlobalTargetInfo t)
    {
        if (t.HasThing) return OwnerFaction(t.Thing);
        if (t.HasWorldObject) return t.WorldObject.Faction;
        return t.Map?.ParentFaction;
    }

    public static bool IsOtherPlayer(Faction f) =>
        f is { IsPlayer: true } && f != Multiplayer.RealPlayerFaction && f != Multiplayer.WorldComp.spectatorFaction;
}

[HarmonyPatch(typeof(Messages), nameof(Messages.Message), typeof(Message), typeof(bool))]
static class HideOtherFactionMessages
{
    static readonly HashSet<Message> hidden = new();
    static bool suppressSound;

    static bool ShouldHide(Message msg)
    {
        if (!MultifactionUiLeaks.Active || !(Multiplayer.Ticking || Multiplayer.ExecutingCmds))
            return false;

        var context = Faction.OfPlayer;
        if (context == Multiplayer.RealPlayerFaction)
            return false;

        // Raised in another player's context
        if (context != Multiplayer.WorldComp.spectatorFaction)
            return true;

        // World tick runs as the spectator, look at what the message points to
        var targets = msg.lookTargets?.targets;
        return targets is { Count: > 0 } && targets.All(t => MultifactionUiLeaks.IsOtherPlayer(MultifactionUiLeaks.OwnerFaction(t)));
    }

    static void Prefix(Message msg, ref bool __state)
    {
        __state = ShouldHide(msg);
        suppressSound = __state;
    }

    // Keep the message in liveMessages, vanilla dedups against it before archiving
    static void Finalizer(Message msg, bool __state)
    {
        suppressSound = false;
        if (__state && Messages.liveMessages.Contains(msg))
            hidden.Add(msg);
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        var play = AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShotOnCamera));
        foreach (var inst in insts)
        {
            if (inst.operand as MethodInfo == play)
                inst.operand = AccessTools.Method(typeof(HideOtherFactionMessages), nameof(PlaySound));
            yield return inst;
        }
    }

    static void PlaySound(SoundDef sound, Map map)
    {
        if (!suppressSound)
            sound.PlayOneShotOnCamera(map);
    }

    [HarmonyPatch(typeof(Messages), nameof(Messages.MessagesDoGUI))]
    static class DrawOnlyVisible
    {
        static void Prefix(ref List<Message> __state)
        {
            if (hidden.Count == 0) return;

            hidden.RemoveWhere(m => !Messages.liveMessages.Contains(m));
            __state = Messages.liveMessages;
            Messages.liveMessages = __state.Where(m => !hidden.Contains(m)).ToList();
        }

        static void Finalizer(List<Message> __state)
        {
            if (__state != null)
                Messages.liveMessages = __state;
        }
    }
}

[HarmonyPatch]
static class HideOtherFactionAlertCulprits
{
    static IEnumerable<MethodBase> TargetMethods() =>
        typeof(Alert).AllSubclassesNonAbstract()
            .Select(t => t.GetMethod(nameof(Alert.GetReport), BindingFlags.Public | BindingFlags.Instance, null, System.Type.EmptyTypes, null))
            .Where(m => m != null && m.DeclaringType != typeof(Alert) && !m.IsAbstract)
            .Distinct();

    static void Postfix(ref AlertReport __result)
    {
        if (!MultifactionUiLeaks.Active || !__result.active) return;

        bool hadCulprits = __result.AnyCulpritValid;
        if (!hadCulprits) return;

        // Copies, the lists can belong to the game (e.g. MapPawns lists)
        if (__result.culpritsPawns != null && __result.culpritsPawns.Any(Other))
            __result.culpritsPawns = __result.culpritsPawns.Where(p => !Other(p)).ToList();
        if (__result.culpritsThings != null && __result.culpritsThings.Any(Other))
            __result.culpritsThings = __result.culpritsThings.Where(t => !Other(t)).ToList();
        if (__result.culpritsCaravans != null && __result.culpritsCaravans.Any(c => MultifactionUiLeaks.IsOtherPlayer(c.Faction)))
            __result.culpritsCaravans = __result.culpritsCaravans.Where(c => !MultifactionUiLeaks.IsOtherPlayer(c.Faction)).ToList();
        if (__result.culpritsTargets != null && __result.culpritsTargets.Any(Other))
            __result.culpritsTargets = __result.culpritsTargets.Where(t => !Other(t)).ToList();
        if (__result.culpritTarget is { } target && Other(target))
            __result.culpritTarget = null;

        if (!__result.AnyCulpritValid)
            __result = AlertReport.Inactive;
    }

    static bool Other(Thing t) => MultifactionUiLeaks.IsOtherPlayer(MultifactionUiLeaks.OwnerFaction(t));
    static bool Other(Pawn p) => Other((Thing)p);
    static bool Other(GlobalTargetInfo t) => t.IsValid && MultifactionUiLeaks.IsOtherPlayer(MultifactionUiLeaks.OwnerFaction(t));
}
