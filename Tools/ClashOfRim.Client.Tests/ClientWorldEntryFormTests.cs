using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.MainMenu;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Protocol;
using ClashOfRim.TestSupport;

internal static class ClientWorldEntryFormTests
{
    internal static void Run() => RunAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Defaults();
                foreach (bool registration in new[] { false, true })
                {
                    foreach (string change in new[] { "close", "user", "server", "colony", "auth", "login", "game", "generation" })
                        await Stale(registration, change);
                    await Submit(registration);
                    await Busy(registration);
                    await InvalidEntry(registration);
                    foreach (string failure in new[] { "identity", "store", "progress" }) await Failure(registration, failure);
                }
                foreach (var (password, confirmation, key) in new[] { ("", "", "PasswordRequired"),
                    ("abc", "ABC", "PasswordMismatch"), ("İ", "i", "PasswordMismatch"),
                    (" 密码 ", "密码", "PasswordMismatch"), ("é", "e\u0301", "PasswordMismatch") })
                    await Validation(password, confirmation, key);
                await EmptyLoginPassword();
                foreach (string effect in new[] { "reentry", "close", "user", "game", "throw" }) await ValidationCallback(effect);
                foreach (string phase in new[] { "confirmation", "registration", "current" }) await MissingAccount(phase);
            }
            WindowBoundary();
            Console.WriteLine("PASS: world entry forms (four cultures; shared entry, registration, ownership, retries and window boundaries)");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static ClientWorldEntryForm Form(ClientWorldEntryStartTests.Fixture f, bool registration)
    {
        var form = registration ? f.Start.CreateRegistrationForm(" https://localhost/base/ ", " 用户-I ") : f.Start.CreateForm();
        form.Password = form.PasswordConfirmation = " 密码-İ ";
        return form;
    }
    private static void Defaults()
    {
        var f = new ClientWorldEntryStartTests.Fixture(); f.Ports.Password = "saved-secret";
        foreach (string server in new[] { "", " ", "https://server/base" })
        {
            f.Ports.Server = server;
            var form = f.Start.CreateForm();
            Check(form.ServerBaseUrl == (string.IsNullOrWhiteSpace(server) ? "http://127.0.0.1:5000" : server)
                && form.UserId == f.Ports.User && form.Password == "" && form.PasswordConfirmation == "", "Entry prefilled credentials or changed defaults.");
            form.Close();
        }
        var registration = f.Start.CreateRegistrationForm("https://registration", "requested-user");
        Check(registration.ServerBaseUrl == "https://registration" && registration.UserId == "requested-user"
            && registration.Password == "" && registration.PasswordConfirmation == "", "Registration borrowed current identity or password.");
        Check(f.Ends == 0 && f.Writes == 0, "Opening a form changed identity.");
    }
    private static async Task Finish(ClientWorldEntryStartTests.Fixture f)
    {
        (await f.Ports.Next())(); await f.Idle();
        Check(f.Hellos == 1 && f.Prepares == 1 && f.Scenarios == 1 && !f.Sync.IsBusy, "Form lost or repeated entry continuation.");
    }
    private static async Task Submit(bool registration)
    {
        var f = new ClientWorldEntryStartTests.Fixture(); var form = Form(f, registration);
        f.On = phase => { if (phase == "progress") form.Close(); };
        f.Change("snapshot");
        Check(form.IsCurrent && form.Password == " 密码-İ ", "Snapshot advancement discarded input.");
        Check(form.Submit(), "Valid form did not start.");
        Check(!form.IsCurrent && form.Password == "" && form.PasswordConfirmation == "", "Accepted form retained its credentials.");
        var token = ClientSessionWork.Token;
        for (int i = 0; i < 20; i++) Check(!form.Submit(), "Accepted form submitted twice.");
        form.Close(); form.Close();
        Check(token == ClientSessionWork.Token && f.Sync.IsBusy && f.Ends == 1 && f.Writes == 1, "Closing a submitted form cancelled its owned entry.");
        await Finish(f);
        Check(f.Create == registration && f.Password == " 密码-İ " && f.Ports.User == (registration ? "用户-I" : "alice")
            && f.Ports.Server == (registration ? "https://localhost/base" : "http://localhost"), "Form changed registration intent or normalization.");
    }
    private static async Task Stale(bool registration, string change)
    {
        var f = new ClientWorldEntryStartTests.Fixture(); var old = Form(f, registration);
        if (change == "close") old.Close(); else f.Change(change);
        var fresh = Form(f, registration); fresh.Password = fresh.PasswordConfirmation = "fresh-secret";
        var context = f.Session.ReadContext(); var token = ClientSessionWork.Token;
        Check(!old.Submit() && !old.IsCurrent && old.Password == "" && old.PasswordConfirmation == "", "Old form crossed " + change);
        Check(f.Ends == 0 && f.Writes == 0 && f.Rejections == 0 && !f.Sync.IsBusy && ClientSessionWork.Token == token
            && f.Ports.Server == context.ServerBaseUrl && f.Ports.User == context.UserId && f.Ports.Colony == context.ColonyId
            && f.Ports.Auth == context.AuthToken && fresh.Password == "fresh-secret", "Old form affected its replacement: " + change);
        Check(fresh.Submit(), "Fresh form could not replace " + change); await Finish(f);
    }
    private static async Task Busy(bool registration)
    {
        var f = new ClientWorldEntryStartTests.Fixture(); var form = Form(f, registration);
        Check(f.Sync.TryBeginManual(out var lease), "Fixture admission failed.");
        using (lease)
        {
            Check(!form.Submit() && form.IsCurrent && form.Password == " 密码-İ " && f.Ends == 0
                && f.Writes == 0 && f.Rejected == "ClashOfRim.Sync.StatusBusy", "Busy form consumed its draft or another operation.");
        }
        Check(form.Submit(), "Busy form could not retry."); await Finish(f);
    }
    private static async Task InvalidEntry(bool registration)
    {
        var f = new ClientWorldEntryStartTests.Fixture(); var form = Form(f, registration);
        form.ServerBaseUrl = "ftp://localhost";
        Check(!form.Submit() && f.Rejected == "ClashOfRim.ServerEntry.StatusAddressMissing", "Form bypassed entry URL validation.");
        form.ServerBaseUrl = "localhost"; form.UserId = " ";
        Check(!form.Submit() && f.Rejected == "ClashOfRim.EnterUserId" && f.Ends == 0 && f.Writes == 0
            && form.IsCurrent && form.Password == " 密码-İ ", "Invalid identity consumed the form.");
        form.UserId = " alice "; Check(form.Submit(), "Corrected form could not submit."); await Finish(f);
    }
    private static async Task Validation(string password, string confirmation, string key)
    {
        var f = new ClientWorldEntryStartTests.Fixture(); var form = Form(f, true);
        form.Password = password; form.PasswordConfirmation = confirmation;
        Check(!form.Submit() && f.Rejected == "ClashOfRim.ServerEntry." + key && f.Rejections == 1
            && f.Ends == 0 && f.Writes == 0 && form.IsCurrent && form.Password == password, "Registration password validation changed.");
        form.Password = form.PasswordConfirmation = "  ";
        Check(form.Submit(), "Form applied new trimming or strength rules."); await Finish(f);
        Check(f.Password == "  ", "Registration password was normalized.");
    }
    private static async Task EmptyLoginPassword()
    {
        var f = new ClientWorldEntryStartTests.Fixture(); f.Ports.Password = "saved-secret";
        var form = f.Start.CreateForm();
        Check(form.Submit(), "Login inherited registration password validation."); await Finish(f);
        Check(f.Password == "" && !f.Create, "Login replaced empty password with saved credentials.");
    }
    private static async Task Failure(bool registration, string phase)
    {
        var f = new ClientWorldEntryStartTests.Fixture(); var form = Form(f, registration); f.ThrowAt = phase;
        Check(!form.Submit(), "Failed entry was accepted."); await f.Idle();
        Check(!form.IsCurrent && form.Password == "" && form.PasswordConfirmation == "" && !f.Sync.IsBusy && f.Failures == 1,
            "Failed identity transition retained an old form or admission: " + phase);
        f.ThrowAt = null; Check(!form.Submit(), "Failed form adopted the changed identity.");
        Check(Form(f, registration).Submit(), "New form could not retry failed entry."); await Finish(f);
    }
    private static async Task ValidationCallback(string effect)
    {
        var f = new ClientWorldEntryStartTests.Fixture(); var form = Form(f, true); form.PasswordConfirmation = "mismatch";
        int callbacks = 0;
        f.On = phase =>
        {
            if (phase != "format") return;
            callbacks++;
            if (effect == "reentry") Check(!form.Submit(), "Validation callback reentered the form.");
            else if (effect == "close") form.Close();
            else if (effect != "throw") f.Change(effect);
        };
        if (effect == "throw") f.ThrowAt = "format";
        try { Check(!form.Submit(), "Invalid form submitted during callback."); Check(effect != "throw", "Fixture did not throw."); }
        catch (InvalidOperationException ex) when (effect == "throw" && ex.Message == "format failed") { }
        f.On = null; f.ThrowAt = null;
        bool keep = effect is "reentry" or "throw";
        Check(f.Ends == 0 && f.Writes == 0 && f.Rejections == (effect == "reentry" ? 1 : 0)
            && form.IsCurrent == keep && callbacks == (effect == "throw" ? 0 : 1), "Validation effect escaped form ownership: " + effect);
        if (!keep) form = Form(f, true);
        form.Password = form.PasswordConfirmation = "retry";
        Check(form.Submit(), "Validation callback retained submit guard."); await Finish(f);
    }
    private static async Task MissingAccount(string phase)
    {
        var f = new ClientWorldEntryStartTests.Fixture();
        f.PrepareResponse = new() { Result = new() { Accepted = false, ErrorCode = (int)ProtocolErrorCode.AccountNotFound } };
        Action? confirm = null; ClientWorldEntryForm? registration = null; int opened = 0;
        f.ConfirmAccount = (account, accept) => { Check(account == "alice", "Confirmation changed account."); confirm = accept; };
        f.RegisterAccount = (server, account) => { opened++; registration = f.Start.CreateRegistrationForm(server, account); };
        Check(f.Start.CreateForm().Submit(), "Missing-account entry did not start.");
        (await f.Ports.Next())(); await f.Idle();
        Check(confirm is not null && !f.Sync.IsBusy && f.Scenarios == 0 && f.Prepares == 1, "Missing-account decision did not release preparation.");
        if (phase == "confirmation") f.Change("login");
        confirm!(); confirm();
        Check(opened == (phase == "confirmation" ? 0 : 1), "Old or repeated confirmation opened registration.");
        if (registration is null) return;
        Check(registration.UserId == "alice" && registration.ServerBaseUrl == "http://localhost"
            && registration.Password == "" && registration.PasswordConfirmation == "", "Registration did not retain the missing account identity.");
        if (phase == "registration") f.Change("login");
        registration.Password = registration.PasswordConfirmation = "create-secret";
        f.PrepareResponse = null;
        bool current = phase == "current";
        Check(registration.Submit() == current, "Opened registration adopted a later login.");
        if (current) { (await f.Ports.Next())(); await f.Idle(); }
        Check(f.Ends == (current ? 2 : 1) && f.Writes == (current ? 2 : 1) && f.Prepares == (current ? 2 : 1)
            && f.Create == current && f.Scenarios == (current ? 1 : 0) && registration.Password == "",
            "Registration duplicated entry work or altered the replacement session.");
    }
    private static void WindowBoundary()
    {
        var windows = new[] { typeof(ClashOfRimServerEntryDialog), typeof(ClashOfRimAccountRegistrationDialog) };
        var types = typeof(ClashOfRimMod).Assembly.GetTypes().Where(t => windows.Contains(t) || t.DeclaringType is { } owner && windows.Contains(owner));
        var violations = ModuleDependencyGuard.FindViolations(types, t => t == typeof(ClashOfRimMod)
            || t == typeof(ClashOfRimSettings) || t == typeof(AIRsLight.ClashOfRim.ClientNetwork.ClashOfRimModNetworkClient)
            || t == typeof(Verse.LoadedModManager) || t == typeof(IServiceProvider));
        // The two existing public constructors are the only compatibility exception.
        var compatibility = windows.Select(t => $"{t.FullName}..ctor depends on {typeof(ClashOfRimMod).FullName}").ToArray();
        Check(violations.OrderBy(v => v).SequenceEqual(compatibility.OrderBy(v => v)), "Entry windows retained a host dependency outside compatibility constructors: " + string.Join("; ", violations));
        Check(windows.All(t => t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single().FieldType == typeof(ClientWorldEntryForm)), "Entry windows retained form state.");
        var focus = windows[0].GetMethod("ResolveNextInputControl", BindingFlags.Static | BindingFlags.NonPublic)!;
        string[] controls = { "ClashOfRim.ServerEntry.ServerAddress", "ClashOfRim.ServerEntry.UserId", "ClashOfRim.ServerEntry.Password" };
        for (int i = 0; i < controls.Length; i++)
            foreach (bool reverse in new[] { false, true })
                Check((string)focus.Invoke(null, new object[] { controls[i], reverse })! == controls[(i + (reverse ? 2 : 1)) % 3], "Entry tab focus changed.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
