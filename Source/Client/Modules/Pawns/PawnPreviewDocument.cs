using System.Linq;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Modules.Pawns;

// Removes links to the receiving world's mutable ownership and relationship
// graphs. The remaining pawn/gear/health/gene state is still loaded by Scribe.
internal static class PawnPreviewDocument
{
    internal static void Project(XElement pawn, string ideologyTrackerClass)
    {
        foreach (var thing in pawn.DescendantsAndSelf().Where(e => e == pawn || e.Element("id") is not null).ToArray())
        {
            Replace(thing, "faction", new XElement("faction", "null"));
            Replace(thing, "questTags", new XElement("questTags"));
        }
        Replace(pawn, "ownership", new XElement("ownership"));
        if (pawn.Element("social") is { } social)
        {
            Replace(social, "directRelations", new XElement("directRelations"));
            Replace(social, "virtualRelations", new XElement("virtualRelations"));
            Replace(social, "pregnancyApproaches", Dictionary("pregnancyApproaches"));
            Replace(social, "relativeInvolvedInRescueQuest", new XElement("relativeInvolvedInRescueQuest", "null"));
        }
        if (pawn.Element("ideo") is { } ideo)
        {
            // Preserve certainty/join time through the normal LoadingVars path,
            // but the preview tracker never runs the native fallback SetIdeo.
            ideo.SetAttributeValue("Class", ideologyTrackerClass);
            Replace(ideo, "ideo", new XElement("ideo", "null"));
            Replace(ideo, "previousIdeos", new XElement("previousIdeos"));
            Replace(ideo, "babyIdeoExposure", new XElement("babyIdeoExposure"));
        }
        if (pawn.Element("royalty") is { } royalty)
        {
            // Titles are restored from global metadata without SetTitle or heir
            // selection. Keep the serialized psycast/ability state on the pawn.
            Replace(royalty, "titles", new XElement("titles"));
            foreach (string name in new[] { "favor", "highestTitles", "heirs" }) Replace(royalty, name, Dictionary(name));
            Replace(royalty, "permits", new XElement("permits"));
        }
    }
    private static XElement Dictionary(string name) => new(name, new XElement("keys"), new XElement("values"));
    private static void Replace(XElement parent, string name, XElement value)
    { parent.Elements(name).Remove(); parent.Add(value); }
}
