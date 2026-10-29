using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tests;

// Compile the actual client code against small headless substitutes. These test
// faction ownership and Scribe wiring, not RimWorld's serializer or Unity UI.
public class FactionLettersTest
{
    [TestCase("NewFactionsHaveIndependentStacks")]
    [TestCase("CapturesCurrentStack")]
    [TestCase("ScribesAndRestoresFactionStack")]
    [TestCase("LegacyFactionGetsEmptyStack")]
    [TestCase("SwitchingFactionRestoresItsLetters")]
    [TestCase("TicksEveryFactionInItsOwnContext")]
    [TestCase("TickFailureRestoresContextAndGuard")]
    [TestCase("SingleFactionUsesVanillaTick")]
    public void FactionLetters(string scenario)
    {
        var setFaction = CSharpSyntaxTree.ParseText(Resource("MultiplayerWorldComp.cs"))
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "SetFaction");
        var sources = new[]
        {
            Resource("FactionWorldData.cs"),
            Resource("FactionLetterStack.cs").Replace("static bool Prefix()", "public static bool Prefix()"),
            Resource("FactionLetterStubs.cs"),
            "using System.Collections.Generic; using RimWorld; using Verse; " +
            "namespace Multiplayer.Client { public class MultiplayerWorldComp { " +
            "public SortedDictionary<int, FactionWorldData> factionData = new(); " + setFaction + " } }",
            Scenarios
        };
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("FactionLetters_" + Guid.NewGuid().ToString("N"),
            sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.That(result.Success, Is.True, string.Join("\n", result.Diagnostics));
        var assembly = Assembly.Load(stream.ToArray());
        try
        {
            assembly.GetType("Scenarios")!.GetMethod(scenario)!.Invoke(null, null);
        }
        catch (TargetInvocationException e)
        {
            throw e.InnerException!;
        }
    }

    private static string Resource(string name)
    {
        using var stream = typeof(FactionLettersTest).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private const string Scenarios = """
        using System;
        using Multiplayer.Client;
        using MP = Multiplayer.Client.Multiplayer;
        using RimWorld;
        using Verse;

        public static class Scenarios
        {
            static void Check(bool condition, string message)
            {
                if (!condition) throw new Exception(message);
            }

            static FactionWorldData AddFaction(int id)
            {
                Current.Game.storyteller ??= new Storyteller(null, null, null);
                var data = FactionWorldData.New(id);
                MP.WorldComp.factionData[id] = data;
                Find.FactionManager.factions[id] = new Faction { loadID = id };
                return data;
            }

            static void Select(int id)
            {
                Faction.OfPlayer = Find.FactionManager.GetById(id);
                MP.WorldComp.SetFaction(Faction.OfPlayer);
            }

            public static void NewFactionsHaveIndependentStacks()
            {
                var host = AddFaction(1);
                var guest = AddFaction(2);
                host.letterStack.letters.Add("Host letter");
                Check(guest.letterStack.letters.Count == 0, "Factions share letters");
            }

            public static void CapturesCurrentStack()
            {
                var host = AddFaction(1);
                Select(1);
                var captured = FactionWorldData.FromCurrent(1);
                Check(ReferenceEquals(captured.letterStack, host.letterStack), "Lost active stack");
            }

            public static void ScribesAndRestoresFactionStack()
            {
                var guest = AddFaction(2);
                guest.letterStack.letters.Add("Guest letter");
                guest.letterStack.queued.Add("Delayed guest letter");
                Scribe.mode = LoadSaveMode.Saving;
                guest.ExposeData();
                Check(ReferenceEquals(Scribe_Deep.values["letterStack"], guest.letterStack),
                    "Faction stack is not deeply scribed");
                var loaded = new FactionWorldData();
                Scribe.mode = LoadSaveMode.LoadingVars;
                loaded.ExposeData();
                Check(ReferenceEquals(loaded.letterStack, guest.letterStack), "Stack was not restored");
                Check(loaded.letterStack.queued.Count == 1, "Delayed letters were lost");
            }

            public static void LegacyFactionGetsEmptyStack()
            {
                var host = AddFaction(1);
                Select(1);
                host.letterStack.letters.Add("Host letter");
                var legacy = new FactionWorldData();
                Scribe.mode = LoadSaveMode.LoadingVars;
                legacy.ExposeData();
                Check(legacy.letterStack != null && legacy.letterStack.letters.Count == 0,
                    "Legacy faction inherited host letters");
                Check(!ReferenceEquals(legacy.letterStack, host.letterStack), "Legacy stack aliases host");
            }

            public static void SwitchingFactionRestoresItsLetters()
            {
                var host = AddFaction(1);
                var guest = AddFaction(2);
                host.letterStack.letters.Add("Host letter");
                guest.letterStack.letters.Add("Guest letter");
                Select(1); // The faction in Game's loaded small components.
                var captured = FactionWorldData.FromCurrent(1);
                MP.WorldComp.factionData[1] = captured;
                Select(2); // Loader.PostLoad restores the joining player's faction.
                Check(Find.LetterStack.letters.Count == 1 && Find.LetterStack.letters[0] == "Guest letter",
                    "Joining player sees host letters");
                Find.LetterStack.letters.Clear();
                Select(1);
                Check(Find.LetterStack.letters.Count == 1, "Guest dismissal changed host letters");
                Select(2);
                Check(Find.LetterStack.letters.Count == 0, "Dismissed guest letter returned");
            }

            public static void TicksEveryFactionInItsOwnContext()
            {
                var host = AddFaction(1);
                var guest = AddFaction(2);
                host.letterStack.queued.Add("Host delayed letter");
                guest.letterStack.queued.Add("Guest delayed letter");
                host.letterStack.onTick = () => Check(Faction.OfPlayer.loadID == 1 &&
                    ReferenceEquals(Find.History, host.history), "Wrong host archive context");
                guest.letterStack.onTick = () => Check(Faction.OfPlayer.loadID == 2 &&
                    ReferenceEquals(Find.History, guest.history), "Wrong guest archive context");
                Select(1);
                Find.LetterStack.LetterStackTick();
                Check(host.letterStack.ticks == 1 && guest.letterStack.ticks == 1, "Not ticked exactly once");
                Check(host.letterStack.letters[0] == "Host delayed letter" &&
                    guest.letterStack.letters[0] == "Guest delayed letter", "Delayed letters crossed factions");
                Check(Faction.OfPlayer.loadID == 1 && ReferenceEquals(Find.LetterStack, host.letterStack),
                    "Tick did not restore original faction");
            }

            public static void TickFailureRestoresContextAndGuard()
            {
                var host = AddFaction(1);
                var guest = AddFaction(2);
                Select(1);
                guest.letterStack.onTick = () => throw new InvalidOperationException("Tick failed");
                try { Find.LetterStack.LetterStackTick(); }
                catch (InvalidOperationException) { }
                Check(Faction.OfPlayer.loadID == 1 && ReferenceEquals(Find.LetterStack, host.letterStack),
                    "Failed tick left guest context installed");
                guest.letterStack.onTick = null;
                Find.LetterStack.LetterStackTick();
                Check(guest.letterStack.ticks == 2, "Reentrancy guard was not reset after failure");
            }

            public static void SingleFactionUsesVanillaTick()
            {
                var host = AddFaction(1);
                var guest = AddFaction(2);
                Select(1);
                MP.GameComp.multifaction = false;
                Find.LetterStack.LetterStackTick();
                Check(host.letterStack.ticks == 1 && guest.letterStack.ticks == 0,
                    "Single-faction game no longer uses vanilla tick");
            }
        }
        """;
}
