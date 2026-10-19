using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientColonyAppearanceTests
{
    private static int scenarios;
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture; scenarios = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Legacy(); SaveAndReset(); Observe(); InvalidAccounts(); PersistenceRetry(); Reentry(); FrozenRead();
                foreach (string change in new[] { "server", "user", "colony", "world-id", "auth", "login", "generation", "game", "world", "target", "unavailable" })
                {
                    StaleWindow(change);
                    foreach (string stage in new[] { "read", "write", "persist", "reason", "registration", "success-text" }) Late(change, stage);
                }
                SnapshotAdvance(); Duplicate();
            }
            Console.WriteLine($"  colony appearance: {scenarios} scenarios checked");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static ColonyAppearanceSelection Selection(string color = "#38A4E8") => new("Color", null, null, color);
    private static void Legacy()
    {
        var account = new ClientColonyAppearanceAccount(" HTTPS://EXAMPLE.TEST/// ", " world|雪 ", " user% ", " colony|1 ");
        Check(ClientColonyAppearanceCodec.AccountKey(account) == "https%3A%2F%2Fexample.test|world%7C%E9%9B%AA|user%25|colony%7C1", "Existing account key changed.");
        var selected = new ColonyAppearanceSelection("Color", "Icon|雪", "", "#38A4E8");
        const string packed = "Color|Icon%7C%E9%9B%AA||%2338A4E8";
        Check(ClientColonyAppearanceCodec.Encode(selected) == packed && ClientColonyAppearanceCodec.Decode(packed) == selected, "Legacy escaped value changed.");
        Check(ClientColonyAppearanceCodec.Decode(packed + "|future") == selected && !ClientColonyAppearanceCodec.Decode("Color|short").HasAny
            && !ClientColonyAppearanceCodec.Decode(null).HasAny && ClientColonyAppearanceCodec.Decode("Color|%GG||red").IconDefName == "%GG", "Legacy malformed/extended value behavior changed.");
        Check(ClientColonyAppearanceCodec.AccountKey(account with { World = "a|b", User = "c" }) != ClientColonyAppearanceCodec.AccountKey(account with { World = "a", User = "b|c" }), "Account delimiters collide.");
        Check(ClientColonyAppearanceCodec.AccountKey(account with { Server = "example.test" }) != ClientColonyAppearanceCodec.AccountKey(account with { Server = "http://example.test" }), "Network URL normalization silently migrated stored keys."); scenarios++;
    }
    private static void SaveAndReset()
    {
        var f = new Fixture(); f.Flow.SaveCurrent(Selection());
        Check(f.Flow.Read() == Selection() && f.Writes == 1 && f.Persists == 1 && f.Registered.Count == 1 && f.Messages == 1, "Save did not persist/register/present.");
        Check(f.Trace.IndexOf("write") < f.Trace.IndexOf("persist") && f.Trace.IndexOf("persist") < f.Trace.IndexOf("registration"), "Preference registration preceded persistence.");
        f.Flow.SaveCurrent(Selection()); Check(f.Writes == 1 && f.Persists == 1 && f.Registered.Count == 2, "Unchanged explicit save rewrote storage or lost explicit resubmission.");
        f.Flow.SaveCurrent(ColonyAppearanceSelection.Empty);
        Check(f.Values.Count == 0 && f.Flow.Capture() is null && f.Registered.Last() is null && f.Persists == 2, "Reset did not remove preference and submit default appearance."); scenarios++;
    }
    private static void Observe()
    {
        var f = new Fixture(); var response = Selection().ToDto()!;
        f.Flow.ObserveServer(f.Flow.CaptureObservation(), response); response.ColorHex = "mutated";
        f.Flow.ObserveServer(f.Flow.CaptureObservation(), Selection().ToDto()); f.Flow.ObserveServer(f.Flow.CaptureObservation(), null); f.Flow.ObserveServer(f.Flow.CaptureObservation(), new());
        Check(f.Flow.Read() == Selection() && f.Persists == 1 && f.Registered.Count == 0 && f.Messages == 0, "Server echo aliased data, rewrote identical settings or resubmitted itself."); scenarios++;
    }
    private static void InvalidAccounts()
    {
        foreach (string missing in new[] { "server", "world-id", "user", "colony" })
        {
            var f = new Fixture(); f.Change(missing, " "); f.Open(); f.Flow.SaveCurrent(Selection()); f.Flow.ObserveServer(f.Flow.CaptureObservation(), Selection().ToDto());
            Check(f.Save is null && f.Writes == 0 && f.Persists == 0 && f.Messages == 0 && !f.Flow.Read().HasAny, "Incomplete account produced a false save: " + missing); scenarios++;
        }
    }
    private static void PersistenceRetry()
    {
        foreach (bool observe in new[] { false, true })
        {
            var f = new Fixture(); f.Open(); f.Once("persist", () => throw new IOException("disk"));
            void Save() { if (observe) f.Flow.ObserveServer(f.Flow.CaptureObservation(), Selection().ToDto()); else f.Save!(Selection()); }
            try { Save(); throw new InvalidOperationException("Persistence failure hidden."); } catch (IOException) { }
            Check(f.Registered.Count == 0 && f.Messages == 0 && f.Persists == 1, "Failed save reported success or registered.");
            Save(); Check(f.Persists == 2 && f.Writes == 1 && f.Messages == (observe ? 0 : 1), "Identical retry did not persist the dirty dictionary."); scenarios++;
        }
        foreach (string phase in new[] { "read", "write" })
        {
            var f = new Fixture(); f.Once(phase, () => throw new IOException("injected"));
            try { f.Flow.SaveCurrent(Selection()); throw new Exception("Storage exception hidden."); } catch (IOException) { }
            Check(f.Registered.Count == 0 && f.Messages == 0, "Storage failure published success.");
            f.Flow.SaveCurrent(Selection()); Check(f.Persists == 1 && f.Messages == 1, "Storage retry did not finish."); scenarios++;
        }
    }
    private static void StaleWindow(string change)
    {
        var f = new Fixture(); f.Open(); f.Change(change); f.Save!(Selection());
        Check(f.Writes == 0 && f.Messages == 0 && f.Registered.Count == 0, "Old dialog saved for a replacement source: " + change); scenarios++;
    }
    private static void Late(string change, string phase)
    {
        var f = new Fixture(); f.Open(); string key = f.Key;
        f.Once(phase, () => f.Change(change)); f.Save!(Selection());
        Check(f.Messages == 0 && f.Values.Keys.All(k => k == key), "Save crossed a reentrant account/target boundary: " + phase + "/" + change);
        Check(f.Registered.Count == (phase is "registration" or "success-text" ? 1 : 0), "Old save continued registration after replacement.");
        Check(f.LaterRegistrations == (phase == "success-text" ? 1 : 0), "Old save continued later subscriber after replacement."); scenarios++;
    }
    private static void SnapshotAdvance()
    {
        var f = new Fixture(); f.Open(); f.Ports.Snapshot = "next"; f.Save!(Selection());
        Check(f.Messages == 1 && f.Flow.Read() == Selection(), "Normal snapshot advancement invalidated account preferences."); scenarios++;
    }
    private static void Duplicate()
    {
        var f = new Fixture(); f.Open(); f.Save!(Selection()); f.Save!(Selection("#fff"));
        Check(f.Registered.Count == 1 && f.Messages == 1 && f.Flow.Read() == Selection(), "Consumed dialog submitted twice."); scenarios++;
    }
    private static void FrozenRead()
    {
        var f = new Fixture(); f.Flow.SaveCurrent(new("Color", "  ", "", "#fff"));
        var dto = f.Flow.Capture()!; Check(dto.IconDefName is null && dto.ColorDefName is null, "Whitespace fields changed wire normalization.");
        dto.ColorHex = "other"; Check(f.Flow.Capture()!.ColorHex == "#fff", "Exported DTO mutated stored preference.");
        f.Flow.RegistrationRequested += (value, _) => { if (value is not null) value.ColorHex = "listener"; };
        ModColonyAppearanceDto? last = null; f.Flow.RegistrationRequested += (value, _) => last = value;
        f.Flow.SaveCurrent(Selection()); Check(last!.ColorHex == Selection().ColorHex, "Subscribers shared mutable appearance."); scenarios++;
    }
    private static void Reentry()
    {
        foreach (string phase in new[] { "read", "write", "persist", "reason" })
        {
            var f = new Fixture(); f.Once(phase, () => f.Flow.SaveCurrent(Selection("#new")));
            f.Flow.SaveCurrent(Selection("#old"));
            Check(f.Flow.Read() == Selection("#new") && f.Registered.Count == 1 && f.Registered[0]!.ColorHex == "#new" && f.Messages == 1,
                "Reentrant save was overwritten or announced by older work: " + phase); scenarios++;
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal sealed class Fixture : IClientColonyAppearanceStore, IClientColonyAppearancePresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientColonyAppearance Flow;
        internal readonly Dictionary<string, string> Values = new();
        internal readonly List<string> Trace = new();
        internal readonly List<ModColonyAppearanceDto?> Registered = new();
        internal string WorldId = "world";
        internal object World = new();
        internal bool Available = true;
        internal Action<ColonyAppearanceSelection>? Save;
        internal int Writes, Persists, Messages, LaterRegistrations, Windows;
        internal ColonyAppearanceSelection? OpenedSelection;
        internal Action<string>? Hook;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports));
            Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "session" });
            Flow = new(Session, this, this);
            Flow.RegistrationRequested += (appearance, reason) => { Registered.Add(appearance); Stage("registration"); };
            Flow.RegistrationRequested += (_, _) => LaterRegistrations++;
        }
        internal string Key => ClientColonyAppearanceCodec.AccountKey(ReadAccount());
        internal IClientColonyAppearanceTarget CreateTarget() => new Target(this, Ports.GameOwner, World);
        internal void Open() => Flow.Open(CreateTarget());
        public ClientColonyAppearanceAccount ReadAccount() => new(Ports.Server, WorldId, Ports.User, Ports.Colony);
        public string? Read(string key) { string? value = Values.GetValueOrDefault(key); Stage("read"); return value; }
        public void Write(string key, string? packed) { Writes++; if (packed is null) Values.Remove(key); else Values[key] = packed; Stage("write"); }
        public void Persist() { Persists++; Stage("persist"); }
        public void Open(string colony, ColonyAppearanceSelection current, Action<ColonyAppearanceSelection> save)
        { Save = save; OpenedSelection = current; Windows++; }
        public string Text(string key, params (string Name, object? Value)[] args) { Stage(key.EndsWith("RegistrationReason", StringComparison.Ordinal) ? "reason" : "success-text"); return key; }
        public void Message(string message, ClientWorldWriteMessage kind) { Messages++; Stage("message"); }
        public void Diagnose(string message) { }
        internal void Once(string phase, Action action) => Hook = value => { if (value == phase) { Hook = null; action(); } };
        private void Stage(string phase) { Trace.Add(phase); Hook?.Invoke(phase); }
        internal void Change(string change, string value = "replacement")
        {
            switch (change)
            {
                case "server": Ports.Server = value; break; case "user": Ports.User = value; break; case "colony": Ports.Colony = value; break;
                case "world-id": WorldId = value; break; case "auth": Ports.Auth = value; break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = value }); break;
                case "generation": ClientSessionWork.Reset(); break; case "game": Ports.GameOwner = new(); break; case "world": World = new(); break;
                case "target": Available = false; break; case "unavailable": Session.BeginUnavailable(Ports.Auth, null); break;
            }
        }
        private sealed record Target(Fixture F, object Game, object World) : IClientColonyAppearanceTarget
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.World);
            public bool IsAvailable => F.Available;
            public string Label => "Colony 雪";
        }
    }
}
