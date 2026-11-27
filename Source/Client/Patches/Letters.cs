using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Patches
{
    // A letter's timeout is stamped with Find.TickManager.TicksGame wherever the letter is made: often a map tick, on
    // that map's clock. It's read by the letter stack UI, which runs on the world clock (ahead of every map, by years
    // for some): TimeoutPassed was already true and LetterStackUpdate removed the letter on the next frame, so letters
    // with a timeout (growth moments, joiner/trade offers...) vanished before the player could see them.
    // Keep every timeout on the world clock instead.
    [HarmonyPatch(typeof(LetterWithTimeout), nameof(LetterWithTimeout.StartTimeout))]
    public static class LetterTimeoutOnWorldClock
    {
        private static bool OnWorldClock =>
            Multiplayer.Client != null && Multiplayer.GameComp.asyncTime && Multiplayer.AsyncWorldTime != null;

        public static int Now => OnWorldClock ? Multiplayer.AsyncWorldTime.worldTicks : Find.TickManager.TicksGame;

        static void Postfix(LetterWithTimeout __instance)
        {
            if (!OnWorldClock) return;
            __instance.disappearAtTick += Now - Find.TickManager.TicksGame;
        }
    }

    // ...and read it on the world clock from any context: the letter stack is drawn inside the map interface (on the
    // viewed map's clock), a letter is received during a map tick (on that map's clock).
    [HarmonyPatch]
    static class LetterTimeoutReadOnWorldClock
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(LetterWithTimeout), nameof(LetterWithTimeout.TimeoutPassed));
            yield return AccessTools.PropertyGetter(typeof(LetterWithTimeout), nameof(LetterWithTimeout.LastTickBeforeTimeout));
            yield return AccessTools.Method(typeof(LetterWithTimeout), "PostProcessedLabel");
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts, MethodBase original)
        {
            var ticksGame = AccessTools.PropertyGetter(typeof(TickManager), nameof(TickManager.TicksGame));
            int replaced = 0;
            foreach (var inst in insts)
            {
                if (inst.Calls(ticksGame))
                {
                    inst.opcode = OpCodes.Call;
                    inst.operand = AccessTools.Method(typeof(LetterTimeoutReadOnWorldClock), nameof(TicksGame));
                    replaced++;
                }
                yield return inst;
            }

            if (replaced == 0)
                Log.Error($"Multiplayer: {original} no longer reads TickManager.TicksGame");
        }

        static int TicksGame(TickManager _) => LetterTimeoutOnWorldClock.Now;
    }

    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.LetterStackUpdate))]
    static class CloseLettersDuringSimulating
    {
        // Close info letters during simulation and for the arbiter instance
        static void Postfix(LetterStack __instance)
        {
            if (Multiplayer.Client == null) return;
            if (!TickPatch.Simulating && !Multiplayer.arbiterInstance) return;

            for (int i = __instance.letters.Count - 1; i >= 0; i--)
            {
                var letter = __instance.letters[i];
                if (letter is ChoiceLetter choice && choice.Choices.Any(c => c.action?.Method == choice.Option_Close.action.Method) && Time.time - letter.arrivalTime > 4)
                    __instance.RemoveLetter(letter);
            }
        }
    }

    [HarmonyPatch]
    static class DismissEventLettersOnCancel
    {
        private static readonly ConditionalWeakTable<Dialog_NodeTreeWithFactionInfo, DiaOption> closeOptions = new();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Dialog_NodeTreeWithFactionInfo), MethodType.Constructor,
            [typeof(DiaNode), typeof(Faction), typeof(bool), typeof(bool), typeof(string)])]
        static void EnableCloseOnCancel(Dialog_NodeTreeWithFactionInfo __instance, DiaNode nodeRoot)
        {
            var closeOption = nodeRoot.options.Find(option =>
                option.resolveTree && !option.disabled &&
                option.action?.Target is ChoiceLetter letter && option.action == letter.Option_Close.action);
            if (closeOption == null) return;

            closeOptions.Add(__instance, closeOption);
            __instance.closeOnCancel = true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Window), nameof(Window.OnCancelKeyPressed))]
        static bool CloseLetter(Window __instance)
        {
            if (__instance is not Dialog_NodeTreeWithFactionInfo dialog || !dialog.closeOnCancel ||
                !closeOptions.TryGetValue(dialog, out var closeOption))
                return true;

            Event.current.Use();
            if (!closeOption.disabled && dialog.curNode.options.Contains(closeOption))
            {
                closeOption.dialog = dialog;
                closeOption.Activate();
            }
            return false;
        }
    }

    [HarmonyPatch]
    static class CloseDialogsForExpiredLetters
    {
        public static Dictionary<Type, (MethodInfo method, FastInvokeHandler handler)> defaultChoices = new();

        public static void RegisterDefaultLetterChoice(MethodInfo method, Type letterType = null)
        {
            if (method == null)
                return;
            if (letterType == null)
            {
                if (method.DeclaringType == null)
                    return;
                letterType = method.DeclaringType;
            }

            var handler = MethodInvoker.GetHandler(method);
            defaultChoices[letterType] = (method, handler);
        }

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(ChoiceLetter), nameof(ChoiceLetter.OpenLetter));
            yield return AccessTools.DeclaredMethod(typeof(ChoiceLetter_GrowthMoment), nameof(ChoiceLetter_GrowthMoment.OpenLetter)); // Not a subtype of ChoiceLetter, only LetterWithTimeout
        }

        static bool Prefix(LetterWithTimeout __instance)
        {
            // The letter is about to be force-shown by LetterStack.LetterStackTick because of expiry
            if (Multiplayer.Ticking
                && __instance.TimeoutActive
                && __instance.disappearAtTick == LetterTimeoutOnWorldClock.Now + 1
                && defaultChoices.TryGetValue(__instance.GetType(), out var tuple))
            {
                if (tuple.method.IsStatic)
                    tuple.handler(null, __instance);
                else
                    tuple.handler(__instance);

                return false;
            }

            return true;
        }

        static void Postfix(LetterWithTimeout __instance)
        {
            var wasArchived = __instance.ArchivedOnly;
            var window = Find.WindowStack.WindowOfType<Dialog_NodeTreeWithFactionInfo>();

            if (window != null)
                window.innerWindowOnGUICached = (_ =>
                {
                    if (__instance.ArchivedOnly != wasArchived)
                        window.Close();
                }) + window.innerWindowOnGUICached;
        }
    }

    // ChoiceLetter_BabyBirth and ChoiceLetter_BabyToChild can be dismissed with right-click, which causes issues in MP if some of the players do it
    [HarmonyPatch(typeof(Letter), nameof(Letter.CanDismissWithRightClick), MethodType.Getter)]
    static class DontDismissBabyLetters
    {
        static bool Prefix(Letter __instance, ref bool __result)
        {
            if (__instance is ChoiceLetter_BabyBirth or ChoiceLetter_BabyToChild)
            {
                __result = false;
                return false;
            }

            return true;
        }
    }

    // The button to apply the choices from growth moment dialog will close the letter right after applying the changes, which puts it into archive.
    // When reading sync data we check for the letter in the letter stack, and not archive - which fails to read the letter.
    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.RemoveLetter))]
    static class DontRemoveGrowthMomentLetter
    {
        static bool Prefix(Letter let) =>
            Multiplayer.Client == null ||
            !IsDrawingGrowthMomentDialog.isDrawing;
    }

    [HarmonyPatch(typeof(Dialog_GrowthMomentChoices), nameof(Dialog_GrowthMomentChoices.DoWindowContents))]
    static class IsDrawingGrowthMomentDialog
    {
        public static bool isDrawing;

        static void Prefix() => isDrawing = true;

        static void Postfix() => isDrawing = false;
    }

    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.OpenAutomaticLetters))]
    static class DontAutoOpenLettersOnTimeout
    {
        static bool Prefix() => Multiplayer.Client == null;
    }
}
