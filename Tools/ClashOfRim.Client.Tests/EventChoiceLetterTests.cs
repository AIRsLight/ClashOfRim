using System;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Protocol;
using HarmonyLib;
using Verse;

internal static class EventChoiceLetterTests
{
    private const string Id = "事件-İ-é";
    internal static void Run()
    {
        using var runtime = new Runtime();
        using var ui = new ClientOverlayTestRuntime(nameof(EventChoiceLetterTests));
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string route in Routes)
                {
                    var (f, letter) = Create(runtime, route);
                    var options = letter.Choices.ToArray();
                    Require(options.Length == 2 && options.All(o => o.resolveTree), "Legacy choice flags changed.");
                    options[Choice(route)].action();
                    options[Choice(route)].action(); options[1 - Choice(route)].action();
                    Require(f.Applied.Count + f.Commands.Count == 1 && runtime.Removed == 1, "Choice submitted or removed twice: " + route);
                    if (Choice(route) == 0 && !route.StartsWith("diplomacy"))
                        Require(f.Confirmed.Single().EventId == Id, "Choice bypassed original confirmation.");
                    else
                        Require(f.Commands.Single() == Command(route), "Wrong decision route: " + route);
                }
            }
            foreach (string stage in new[] { "click", "label" })
            foreach (string route in Routes)
            foreach (string change in Changes)
            {
                var (f, letter) = Create(runtime, route);
                if (stage == "label") ui.Translating = _ => { ui.Translating = null; Change(change, f, letter, runtime); };
                var option = letter.Choices.ToArray()[Choice(route)];
                if (stage == "click") Change(change, f, letter, runtime);
                option.action();
                bool expected = change is "none" or "snapshot";
                Require(f.Applied.Count + f.Commands.Count == (expected ? 1 : 0), $"Stale choice dispatched: {stage}/{route}/{change}");
                Require(runtime.Removed == (expected ? 1 : 0), $"Wrong letter removed: {stage}/{route}/{change}");
                ui.Reset();
            }
            foreach (string route in Routes)
            foreach (string change in Changes.Where(c => c is not "none" and not "group"))
            {
                var (f, letter) = Create(runtime, route);
                Action replace = () => Change(change, f, letter, runtime);
                if (Choice(route) == 0 && !route.StartsWith("diplomacy")) f.OnFormat = replace;
                else f.OnReason = replace;
                letter.Choices.ToArray()[Choice(route)].action();
                Require(f.Applied.Count + f.Commands.Count == 0 && !f.Mutations.IsPending && !f.Sync.IsBusy,
                    $"Presentation borrowed a replacement owner: {route}/{change}");
                Require(runtime.Removed == 0, "Stale presentation removed letter.");
            }
            RetryAndReentry(runtime);
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static readonly string[] Routes = { "gift-accept", "gift-reject", "support-accept", "support-reject", "return-accept", "diplomacy-accept", "diplomacy-reject" };
    private static readonly string[] Changes = { "none", "snapshot", "user", "login", "generation", "server", "auth", "colony", "game", "removed", "event", "binding", "unavailable", "no-game", "group" };
    private static int Choice(string route) => route.EndsWith("reject") ? 1 : 0;
    private static string Command(string route) => route switch
    {
        "gift-reject" => "reject-gift:" + Id, "support-reject" => "reject-support:" + Id,
        "diplomacy-accept" => "diplomacy:True:" + Id, "diplomacy-reject" => "diplomacy:False:" + Id,
        _ => throw new InvalidOperationException(route)
    };
    private static (EventApplicationModuleTests.Fixture, ClashOfRimEventChoiceLetter) Create(Runtime runtime, string route)
    {
        runtime.Reset();
        var f = new EventApplicationModuleTests.Fixture();
        var kind = route.StartsWith("support") ? ClientEventApplicationKind.SupportArrival
            : route.StartsWith("return") ? ClientEventApplicationKind.SupportReturn : ClientEventApplicationKind.Gift;
        f.Add(Id, kind, route.StartsWith("diplomacy") ? ServerEventType.AllianceRequest : ServerEventType.ItemDelivery);
        ClashOfRimEventChoiceLetter.Attach(f.Module);
        var letter = new ClashOfRimEventChoiceLetter { EventId = Id, CanAccept = true, CanReject = true };
        runtime.Stack.LettersListForReading.Add(letter);
        return (f, letter);
    }
    private static void Change(string change, EventApplicationModuleTests.Fixture f, ClashOfRimEventChoiceLetter letter, Runtime runtime)
    {
        switch (change)
        {
            case "user": f.Ports.User = "bob"; break;
            case "game": Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game)); break;
            case "removed": runtime.Stack.LettersListForReading.Remove(letter); break;
            case "event": letter.EventId = "other"; f.Add("other"); break;
            case "binding": ClashOfRimEventChoiceLetter.Attach(null); break;
            case "unavailable": f.Session.BeginUnavailable(null, null); break;
            case "no-game": f.Ports.HasGame = false; break;
            case "group": f.Inbox.ForgetGroup(Id); break;
            default: f.Replace(change); break;
        }
    }
    private static void RetryAndReentry(Runtime runtime)
    {
        var (f, letter) = Create(runtime, "gift-accept");
        letter.Actions.Add(ClashOfRimEventLetterActionKind.Accept); letter.CanAccept = false;
        var option = letter.Choices.First();
        f.Sync.TryAcquireSnapshot(out var busy);
        option.action();
        Require(f.Applied.Count == 0 && runtime.Removed == 0, "Busy choice consumed its letter.");
        busy!.Dispose(); option.action();
        Require(f.Applied.Single() == Id && runtime.Removed == 1, "Rejected admission could not retry.");

        (f, letter) = Create(runtime, "diplomacy-accept");
        var options = letter.Choices.ToArray();
        f.OnReason = () => options[1].action();
        options[0].action();
        Require(f.Commands.SequenceEqual(new[] { "diplomacy:True:" + Id }), "Reentrant opposite decision escaped selection.");

        (f, letter) = Create(runtime, "gift-accept");
        f.Inbox.ReplaceDetails(Array.Empty<ModEventDetailDto>());
        option = letter.Choices.First(); option.action();
        Require(f.Refreshes == 1 && runtime.Removed == 0, "Missing detail should refresh and retain the letter.");

        (f, letter) = Create(runtime, "gift-accept");
        ClashOfRimEventChoiceLetter.Attach(null);
        option = letter.Choices.First(); ClashOfRimEventChoiceLetter.Attach(f.Module); option.action();
        Require(f.Applied.Count == 0 && runtime.Removed == 0, "Unbound option adopted a later composition.");

        (f, letter) = Create(runtime, "gift-accept");
        option = letter.Choices.First(); f.Replace("login"); option.action();
        letter.Choices.First().action();
        Require(f.Applied.Count == 1 && runtime.Removed == 1, "Reopening a surviving letter after login could not continue.");

        (f, letter) = Create(runtime, "support-accept");
        f.OnHandled = () => runtime.Stack.LettersListForReading.Remove(letter);
        letter.Choices.First().action();
        Require(f.Confirmed.Single().EventId == Id && runtime.Removed == 0,
            "Original support letter removal blocked confirmation or removed the letter again.");
    }
    private sealed class Runtime : IDisposable
    {
        private static Runtime? current;
        private readonly Game priorGame = Current.Game;
        private readonly object? priorBinding = AccessTools.Field(typeof(ClashOfRimEventChoiceLetter), "applications").GetValue(null);
        private readonly Harmony harmony = new(nameof(EventChoiceLetterTests));
        internal LetterStack Stack = new();
        internal int Removed;
        internal Runtime()
        {
            current = this;
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.LetterStack)), prefix: new HarmonyMethod(typeof(Runtime), nameof(GetStack)));
            harmony.Patch(AccessTools.Method(typeof(LetterStack), nameof(LetterStack.RemoveLetter), new[] { typeof(Letter) }), prefix: new HarmonyMethod(typeof(Runtime), nameof(Remove)));
        }
        internal void Reset() { Stack = new(); Removed = 0; Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game)); }
        private static bool GetStack(ref LetterStack __result) { __result = current!.Stack; return false; }
        private static bool Remove(LetterStack __instance, Letter __0)
        { Require(ReferenceEquals(__instance, current!.Stack), "Removal used a different stack."); current.Removed++; __instance.LettersListForReading.Remove(__0); return false; }
        public void Dispose()
        {
            harmony.UnpatchAll(harmony.Id); Current.Game = priorGame; current = null;
            ClashOfRimEventChoiceLetter.Attach((ClientEventApplications?)priorBinding);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
