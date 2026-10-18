using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientWorldLoginPublicationTests
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
                foreach (bool reconnect in new[] { false, true })
                {
                    Basic(reconnect); NewerObservation(reconnect, true); NewerObservation(reconnect, false); CallbackEdit(reconnect); PriorSessionView(reconnect);
                    foreach (string change in new[] { "server", "user", "colony", "auth", "login", "generation", "world-id", "game", "reset", "snapshot", "unavailable" })
                        foreach (string phase in new[] { "request", "accepted", "queued" }) Ownership(reconnect, phase, change);
                    foreach (string stage in new[] { "identity", "accepted" }) Replacement(reconnect, stage);
                }
                ForeignProof(); CanonicalIdentity(); Rejected();
            }
            Console.WriteLine($"  world login publication: {scenarios} scenarios checked");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static void Basic(bool reconnect)
    {
        var f = new Fixture(); var source = f.Flow.Capture(); var response = Login();
        var accepted = f.Accept(source, response, reconnect)!;
        Check(accepted is not null && accepted.Observation.Number == source.Number && f.Flow.IsCurrent(accepted), "Login did not retain its request order.");
        response.WorldMapMarkers!.Markers[0].Label = "mutated";
        f.Flow.Publish(accepted!, "accepted"); f.Flow.Publish(accepted!, "duplicate"); f.Drain();
        Check(f.Label == "original" && f.P.Trace.Count(s => s == "markers") == 1, "Login receipt was mutable or projected twice."); scenarios++;
    }
    private static void NewerObservation(bool reconnect, bool whileLoggingIn)
    {
        var f = new Fixture(); var source = f.Flow.Capture();
        void Refresh()
        {
            var newer = f.P.Markers.CaptureObservation();
            f.P.Markers.Observe(newer, ClientWorldPublicationTests.Markers("newer"), immediately: true);
        }
        if (whileLoggingIn) f.Session.IdentityChanged += _ => Refresh();
        var accepted = f.Accept(source, Login(), reconnect)!;
        if (!whileLoggingIn) Refresh(); // e.g. a refresh completes during initial snapshot upload.
        f.Flow.Publish(accepted, "late-login"); f.Drain();
        Check(f.Label == "newer", "Login response borrowed a fresh sequence after authentication/upload."); scenarios++;
    }
    private static void CallbackEdit(bool reconnect)
    {
        var f = new Fixture();
        f.Session.IdentityChanged += response => { response!.AuthToken = "mutated"; response.WorldMapMarkers!.Markers[0].Label = "callback"; };
        var accepted = f.Accept(f.Flow.Capture(), Login(), reconnect)!;
        f.Flow.Publish(accepted, "callbacks"); f.Drain();
        Check(f.Label == "original" && f.Ports.Auth == "token", "Identity callback modified the accepted credential or markers."); scenarios++;
    }
    private static void PriorSessionView(bool reconnect)
    {
        var f = new Fixture(); var source = f.Flow.Capture();
        f.P.Markers.Observe(f.P.Markers.CaptureObservation(), ClientWorldPublicationTests.Markers("previous-session"), immediately: true);
        var accepted = f.Accept(source, Login(), reconnect)!;
        f.Flow.Publish(accepted, "new-session"); f.Drain();
        Check(f.Label == "original", "An invalidated old-session view blocked the accepted new-session response."); scenarios++;
    }
    private static void Ownership(bool reconnect, string phase, string change)
    {
        var f = new Fixture(); var source = f.Flow.Capture();
        if (phase == "request") f.Change(change);
        var accepted = f.Accept(source, Login(), reconnect);
        bool validBefore = change is "snapshot" or "unavailable" || phase != "request";
        if (!validBefore)
        { Check(accepted is null && f.Label is null, "Stale login request adopted " + change); scenarios++; return; }
        Check(accepted is not null, "Login did not recover an expired source or permit snapshot advancement.");
        if (phase == "accepted") f.Change(change);
        f.Flow.Publish(accepted!, "pending");
        if (phase == "queued") f.Change(change);
        f.Drain();
        bool applied = phase == "request" || change == "snapshot";
        Check(f.P.Trace.Contains("markers") == applied, "Login projection crossed " + phase + "/" + change); scenarios++;
    }
    private static void Replacement(bool reconnect, string phase)
    {
        var f = new Fixture(); bool reentered = false;
        if (phase == "identity") f.Session.IdentityChanged += _ =>
        {
            if (reentered) return; reentered = true;
            f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "new-token", SessionId = "new-session" });
        };
        var accepted = f.Accept(f.Flow.Capture(), Login(), reconnect);
        if (phase == "accepted") f.Change("login");
        if (accepted is not null) f.Flow.Publish(accepted, "obsolete");
        f.Drain();
        Check(f.Ports.Auth == "new-token" && !f.P.Trace.Contains("markers") && (phase != "identity" || accepted is null), "Old login adopted reentrant authentication."); scenarios++;
    }
    private static void ForeignProof()
    {
        var f = new Fixture(); var source = f.Flow.Capture(); f.Ports.User = "other";
        var login = f.Session.PublishLogin(Login())!;
        Check(f.Flow.Adopt(source, login, Login()) is null, "Unrelated login proof rebound an old request."); scenarios++;
    }
    private static void CanonicalIdentity()
    {
        var f = new Fixture(); var response = Login(); response.AuthenticatedUserId = "  canonical-user  ";
        var accepted = f.Flow.Accept(f.Flow.Capture(), response)!;
        f.Flow.Publish(accepted, "canonical"); f.Drain();
        Check(accepted.Login.Context.UserId == "canonical-user" && accepted.Observation.Scope.Context.UserId == "canonical-user" && f.Label == "original", "Server-assigned canonical identity lost publication."); scenarios++;
    }
    private static void Rejected()
    {
        var f = new Fixture(); var source = f.Flow.Capture(); var response = Login(); response.Result!.Accepted = false;
        Check(f.Flow.Accept(source, response) is null && f.Ports.Auth == "auth" && f.Flow.IsCurrent(source), "Rejected login changed authentication."); scenarios++;
    }
    private static ModLoginResponseDto Login() => new()
    { Result = new() { Accepted = true }, AuthenticatedUserId = "alice", AuthToken = "token", SessionId = "session", WorldMapMarkers = ClientWorldPublicationTests.Markers("original") };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientWorldPublicationTestPorts P;
        internal readonly ClientWorldLoginPublication Flow;
        internal string? Label => P.Markers.Snapshot.SingleOrDefault()?.Label;
        internal Fixture()
        { ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports)); P = new(Session, Ports); Flow = new(Session, P.Markers); }
        internal ClientWorldLoginPublication.Accepted? Accept(ClientWorldMarkers.Observation source, ModLoginResponseDto response, bool reconnect)
        {
            if (!reconnect) return Flow.Accept(source, response);
            var login = Session.PublishLogin(source.Scope.Owner, source.Scope.Context, response);
            return login is null ? null : Flow.Adopt(source, login, response);
        }
        internal void Drain() => Ports.PumpUntil(Task.CompletedTask).GetAwaiter().GetResult();
        internal void Change(string change)
        {
            switch (change)
            {
                case "server": Ports.Server = "http://other"; break; case "user": Ports.User = "other"; break;
                case "colony": Ports.Colony = "other"; break; case "auth": Ports.Auth = "other"; break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "new-token", SessionId = "new-session" }); break;
                case "generation": ClientSessionWork.Reset(); break; case "world-id": P.LocalWorldId = "other"; break;
                case "game": Ports.GameOwner = new(); break; case "reset": P.Markers.Reset(); break;
                case "snapshot": Ports.Snapshot = "advanced"; break; case "unavailable": Session.BeginUnavailable(Ports.Auth, null); break;
            }
        }
    }
}
