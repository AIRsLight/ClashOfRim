using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Presence;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;

using Fixture = ClientFollowupTestPorts;

internal static class ClientSessionFollowupTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture; int cases = 0;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (ClientSessionFollowupSource source in Enum.GetValues<ClientSessionFollowupSource>())
                foreach (ClientSessionFollowup effect in Enum.GetValues<ClientSessionFollowup>())
                {
                    if (!Used(effect, source)) continue;
                    await Dispatch(effect, source, "none", "before"); cases++;
                    foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "game", "world", "snapshot", "password" })
                    { await Dispatch(effect, source, change, "before"); cases++; }
                    if (effect is ClientSessionFollowup.Events or ClientSessionFollowup.Markers
                        || effect == ClientSessionFollowup.ColonySites && source != ClientSessionFollowupSource.InitialWorld)
                        foreach (string change in new[] { "login", "generation", "game", "world", "snapshot", "password" })
                        { await Dispatch(effect, source, change, "text"); cases++; }
                    if (effect == ClientSessionFollowup.ColonySites)
                        foreach (string change in new[] { "login", "generation", "game", "world", "snapshot", "password" })
                        { await Dispatch(effect, source, change, "appearance"); cases++; }
                }
                Queue(); cases++;
                foreach (string stage in new[] { "text", "appearance" }) { await CallbackFailure(stage); cases++; }
            }
            Console.WriteLine($"PASS: session followup dispatch ({cases} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }
    private static bool Used(ClientSessionFollowup effect, ClientSessionFollowupSource source) => source switch
    {
        ClientSessionFollowupSource.InitialWorld => true,
        ClientSessionFollowupSource.ManualLogin => effect is ClientSessionFollowup.Events or ClientSessionFollowup.Players,
        _ => effect is not (ClientSessionFollowup.Markers or ClientSessionFollowup.Extensions)
    };
    private static async Task Dispatch(ClientSessionFollowup effect, ClientSessionFollowupSource source, string change, string stage)
    {
        var f = new Fixture(source); bool reached = stage == "before";
        if (stage == "before") f.Change(change);
        else f.Hook = point => { if (point == stage) { reached = true; f.Hook = null; f.Change(change); } };
        await f.Ports.PumpUntil(f.Module.Dispatch(effect, source, f.Step));
        bool allowed = change is "none" or "password" || change == "snapshot" && source == ClientSessionFollowupSource.ManualLogin;
        Require(reached && f.Calls.Count == (allowed ? 1 : 0), "Dispatch escaped source or skipped domain: " + effect + "/" + source + "/" + stage + "/" + change);
        if (!allowed) return;
        string expected = effect.ToString();
        Require(f.Calls.Single() == expected, "Wrong destination domain: " + effect);
        if (effect == ClientSessionFollowup.Chat) Require(f.ChatAfter == 0 && f.ChatLimit == 20, "Initial chat used incremental history.");
        if (effect == ClientSessionFollowup.ColonySites)
        {
            Require(f.SiteAppearance?.ColorHex == "#123456" && f.SiteCount == 1 && !f.Suppressed, "Colony followup lost appearance/sites/notification semantics.");
            Require(f.RegistrationRequested == (source != ClientSessionFollowupSource.InitialWorld), "Map request and initial immediate registration were conflated.");
        }
        if (effect == ClientSessionFollowup.Extensions) Require(f.SiteCount == 0 && f.ExtensionCount == 1 && !f.RegistrationRequested, "Extension-only followup registered colony sites.");
        string? key = effect switch
        {
            ClientSessionFollowup.Events => source == ClientSessionFollowupSource.ManualLogin ? "ClashOfRim.Login.ReasonManual"
                : source == ClientSessionFollowupSource.MapLogin ? "ClashOfRim.MapSession.ReasonLogin"
                : source == ClientSessionFollowupSource.SnapshotReload ? "ClashOfRim.ServerEntry.ReasonServerSnapshotLoaded" : "ClashOfRim.WorldBaseline.ReasonInitialSnapshotUploaded",
            ClientSessionFollowup.Markers => "ClashOfRim.WorldBaseline.ReasonSubmitted",
            ClientSessionFollowup.ColonySites when source == ClientSessionFollowupSource.MapLogin => "ClashOfRim.MapSession.ReasonLoginSucceeded",
            ClientSessionFollowup.ColonySites when source == ClientSessionFollowupSource.SnapshotReload => "ClashOfRim.ServerEntry.ReasonServerSnapshotLoaded",
            _ => null
        };
        Require(f.Keys.SequenceEqual(key is null ? Array.Empty<string>() : new[] { key }), "Wrong localized followup reason.");
        if (effect == ClientSessionFollowup.Events) Require(f.Reason == CultureInfo.CurrentCulture.Name + ":" + key, "Translated reason was not delivered to the event workflow.");
        Require(!f.Sync.IsBusy, "Followup leaked synchronization admission.");
    }
    private static void Queue()
    {
        var f = new Fixture(ClientSessionFollowupSource.MapLogin);
        var queue = new ModEventQueueSummaryDto { DirectlyProcessable = new() { new() { EventId = "event" } } };
        f.Module.AdoptQueue(queue); queue.DirectlyProcessable.Clear();
        Require(f.Inbox.CopyQueuedIds().SequenceEqual(new[] { "event" }), "Accepted queue did not reach the real inbox independently of its DTO.");
    }
    private static async Task CallbackFailure(string stage)
    {
        var f = new Fixture(ClientSessionFollowupSource.MapLogin);
        f.Hook = point => { if (point == stage) throw new InvalidOperationException("callback"); };
        bool threw = false;
        try { await f.Ports.PumpUntil(f.Module.Dispatch(ClientSessionFollowup.ColonySites, ClientSessionFollowupSource.MapLogin, f.Step)); }
        catch (InvalidOperationException ex) when (ex.Message == "callback") { threw = true; }
        Require(threw && f.Calls.IsEmpty && !f.Sync.IsBusy, "Failed preparation still started a followup.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
