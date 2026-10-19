using System;
using System.Collections;
using System.Collections.Generic;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;

internal static class RemoteFactionProjectionTests
{
    internal static void Run()
    {
        var source = XDocument.Parse("<savegame><game><world><factionManager><allFactions>"
            + "<li><def>OutlanderCivil</def><relations><li><other>Faction_16</other></li></relations></li>"
            + "<li><def>Pirate</def><loadID>17</loadID><relations><li><other>Faction_16</other><kind>Hostile</kind></li></relations></li>"
            + "<li><def>Invalid</def><loadID>not-an-id</loadID></li><li IsNull=\"True\" />"
            + "</allFactions></factionManager></world></game></savegame>");
        var parsed = (IDictionary)AccessTools.Method(typeof(RemoteMapSnapshotProjector), "ReadSourceFactions").Invoke(null, new object[] { source })!;
        Check(parsed.Count == 2 && parsed.Contains("Faction_0") && parsed.Contains("Faction_17"), "Default faction ID was lost or malformed ID became zero.");
        object zero = parsed["Faction_0"]!;
        var relations = (IReadOnlyDictionary<string, FactionRelationKind>)zero.GetType().GetProperty("Relations")!.GetValue(zero)!;
        Check(relations["Faction_16"] == FactionRelationKind.Neutral, "Omitted Scribe relation kind did not preserve Neutral.");
        var readId = AccessTools.Method(typeof(RemoteMapSnapshotProjector), "ReadFactionLoadId");
        Check((string)readId.Invoke(null, new object[] { XElement.Parse("<li><loadID>0</loadID></li>") })! == "Faction_0", "Explicit zero differs from omitted zero.");
        Check(readId.Invoke(null, new object[] { XElement.Parse("<li><loadID>-1</loadID></li>") }) is null, "Invalid negative faction ID accepted.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
