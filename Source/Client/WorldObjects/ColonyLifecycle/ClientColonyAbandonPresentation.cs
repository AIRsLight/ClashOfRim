using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.World;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientColonyAbandonPresentation : ClientWorldWritePresentation, IClientColonyAbandonPresentation
{
    internal ClientColonyAbandonPresentation(Action<string> status) : base(status) { }
    public void Confirm(string colony, Func<Task> confirm) => Find.WindowStack.Add(new AbandonPlayerColonyConfirmationWindow(colony, confirm));
}
