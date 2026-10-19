namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void SubmitInitialWorldConfigurationIfPending()
        => _ = initialWorldSetup.StartAsync(colonyAppearance.Capture());

}
