using System.Text.Json;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Multiplayer;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using HarmonyLib;
using RimTest.Protocol;
using RimTest.Runtime;
using RimWorld;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal sealed partial class DomainCommands
{
    private static readonly Type ShopEditor = typeof(ServerShopListingDialogWindow);
    private void RegisterCatalog()
    {
        bridge.Router.Register("clash.shop.catalog", "Enumerate every definition offered by the original shop editor, without its UI search display limit.", "observe", a =>
            ObjectInspector.Paginate(((IEnumerable<ThingDef>)InvokeStatic(ShopEditor, "SearchThingDefs", "")!).OrderBy(d => d.defName, StringComparer.Ordinal)
                .Select(d => new { defName = d.defName, category = d.category.ToString(), type = d.thingClass.FullName }).ToArray(), a));
        bridge.Router.Register("clash.shop.catalogTemplate", "Use original editor default metadata and concrete offered stuff, quality, durability and gene selections. No listing is published.", "semantic", a =>
        {
            Loopback();
            var def = ((IEnumerable<ThingDef>)InvokeStatic(ShopEditor, "SearchThingDefs", a.String("defName"))!).Single(d => d.defName == a.String("defName"));
            var item = (ModThingReferenceDto)InvokeStatic(ShopEditor, "BuildItemReference", def)!;
            bool variant = a.Bool("variant");
            if (def.MadeFromStuff)
            {
                var stuff = ((IEnumerable<ThingDef>)InvokeStatic(ThingUtility, "AllowedStuffDefs", def)!).OrderBy(d => d.defName, StringComparer.Ordinal).First();
                InvokeStatic(ShopEditor, "SetItemStuff", item, stuff.defName);
            }
            if (variant && item.Quality is not null) InvokeStatic(ShopEditor, "SetItemQuality", item, QualityCategory.Masterwork.ToString());
            if (variant && item.HitPoints is int hp) InvokeStatic(ShopEditor, "SetItemHitPoints", item, Math.Max(1, hp * 73 / 100));
            var core = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.CoreCompatibility.CoreThingReferenceMetadata", true)!;
            if ((bool)InvokeStatic(core, "IsSkillBookDef", def)!) InvokeStatic(core, "SetBookSkillDefNames", item, new[] { "Shooting", "Cooking" });
            if ((bool)InvokeStatic(core, "IsResearchBookDef", def)!)
            {
                var research = ((IEnumerable<ResearchProjectDef>)InvokeStatic(core, "ResearchBookProjects", def)!).OrderBy(d => d.defName, StringComparer.Ordinal).First();
                InvokeStatic(core, "SetResearchProjectDefName", item, research.defName);
            }
            var trainer = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.Traits.TraitTrainerUtility", true)!;
            if ((bool)InvokeStatic(trainer, "IsTraitTrainerDef", def)!)
                InvokeStatic(trainer, "SetTrait", item, TraitDefOf.Kind, 0);
            if (ModsConfig.RoyaltyActive && def.HasComp<CompBladelinkWeapon>())
            {
                var royalty = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.DlcCompatibility.RoyaltyThingReferenceCompatibility", true)!;
                item.UniqueWeaponTraits = new List<string> { ((IEnumerable<WeaponTraitDef>)InvokeStatic(royalty, "MatchingTraits")!).OrderBy(d => d.defName, StringComparer.Ordinal).First().defName };
            }
            if (ModsConfig.OdysseyActive && def.HasComp<CompUniqueWeapon>())
            {
                var odyssey = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.DlcCompatibility.OdysseyThingReferenceCompatibility", true)!;
                var trait = ((IEnumerable<WeaponTraitDef>)InvokeStatic(odyssey, "MatchingTraits", def)!).OrderBy(d => d.defName, StringComparer.Ordinal)
                    .First(t => (bool)InvokeStatic(odyssey, "CanAddTrait", def, item, t)!);
                item.UniqueWeaponTraits = new List<string> { trait.defName };
                InvokeStatic(odyssey, "RefreshUniqueWeaponMarketValue", def, item);
            }
            if (ModsConfig.BiotechActive && typeof(GeneSetHolderBase).IsAssignableFrom(def.thingClass))
            {
                var biotech = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.DlcCompatibility.BiotechCompatibility", true)!;
                var genes = DefDatabase<GeneDef>.AllDefsListForReading.Where(g => g.biostatArc == 0 && !g.defName.StartsWith("Hair_", StringComparison.Ordinal)).OrderBy(g => g.defName, StringComparer.Ordinal).Take(2).Select(g => g.defName).ToArray();
                InvokeStatic(biotech, "SetGeneDefNames", item, genes);
            }
            bool complete = (bool)InvokeStatic(ShopEditor, "HasCompleteItem", item, ThingReferenceSurfaces.ServerShopListing)!;
            return new { reference = bridge.Objects.Add(item), data = item, complete, defName = def.defName };
        });
        bridge.Router.Register("clash.shop.listing", "Look up one current shop view-model record by listing ID; does not refresh or cache objects not selected.", "observe", a =>
        {
            var listings = (IEnumerable<ModServerShopListingDto>)AccessTools.Property(typeof(ClashOfRimMod), "ServerShopListingsSnapshot").GetValue(Mod)!;
            var item = listings.SingleOrDefault(l => l.ListingId == a.String("listingId"));
            return new { count = listings.Count(), listing = item is null ? null : Record(item) };
        });
        bridge.Router.Register("clash.shop.received", "Compare spawned inventory directly with a concrete shop template; no transfer serialization is used.", "observe", a =>
        {
            // Bind only a previously observed template; the external runner cannot inject arbitrary DTOs.
            var item = bridge.Objects.Get<ModThingReferenceDto>(a.String("template"));
            var def = item.MinifiedInnerDefName ?? item.DefName;
            var things = StabilityMap(a).listerThings.AllThings.Where(t => (t is MinifiedThing m ? m.InnerThing?.def.defName : t.def.defName) == def).ToArray();
            bool Match(Thing t)
            {
                var inner = t is MinifiedThing m ? m.InnerThing : t;
                return inner is not null && (item.StuffDefName ?? item.MinifiedInnerStuffDefName) == inner.Stuff?.defName
                    && (item.Quality is null || inner.TryGetComp<CompQuality>()?.Quality.ToString() == item.Quality)
                    && (item.HitPoints is null || inner.HitPoints + AmbientDamageObservation.Loss(inner) == Math.Min(item.HitPoints.Value, inner.MaxHitPoints))
                    && (item.WornByCorpse is null || (inner as Apparel)?.WornByCorpse == item.WornByCorpse)
                    && MatchesMetadata(item, inner)
                    && (!item.Metadata.TryGetValue("rimworld.biotech.geneDefNames", out string? genes) || inner is GeneSetHolderBase { GeneSet: not null } holder &&
                        string.Join(",", holder.GeneSet.GenesListForReading.Select(g => g.defName).Order(StringComparer.Ordinal)) ==
                        string.Join(",", genes!.Split(',').Select(s => s.Trim()).Order(StringComparer.Ordinal)));
            }
            return new { total = things.Sum(t => t.stackCount), matching = things.Where(Match).Sum(t => t.stackCount), facts = things.Take(8).Select(ThingState).ToArray() };
        });
    }
    private static bool MatchesMetadata(ModThingReferenceDto item, Thing thing)
    {
        if (item.Metadata.TryGetValue("clashofrim.core.bookSkillDefNames", out string? skills))
        {
            if (thing is not Book book || !book.BookComp.TryGetDoer(out BookOutcomeDoerGainSkillExp doer)
                || !skills!.Split(',').Order(StringComparer.Ordinal).SequenceEqual(doer.Values.Keys.Select(s => s.defName).Order(StringComparer.Ordinal))) return false;
        }
        if (item.Metadata.TryGetValue("clashofrim.core.researchProjectDefName", out string? project))
        {
            if (thing is not Book book || !book.BookComp.TryGetDoer(out ReadingOutcomeDoerGainResearch doer)
                || !Field<Dictionary<ResearchProjectDef, float>>(doer, "values").Keys.Select(p => p.defName).SequenceEqual(new[] { project })) return false;
        }
        if (item.Metadata.TryGetValue("clashofrim.traitTrainer.traitDefName", out string? trait))
        {
            var effect = thing.TryGetComp<AIRsLight.ClashOfRim.Traits.CompUseEffectAddTrait>();
            if (effect is null || effect.traitDefName != trait || effect.traitDegree.ToString(System.Globalization.CultureInfo.InvariantCulture) != item.Metadata["clashofrim.traitTrainer.traitDegree"]) return false;
        }
        if (item.UniqueWeaponTraits?.Count > 0)
        {
            // Observe the native component's trait list rather than serializing another DTO.
            var traits = thing.TryGetComp<CompBladelinkWeapon>()?.TraitsListForReading ?? thing.TryGetComp<CompUniqueWeapon>()?.TraitsListForReading;
            if (traits is null || !traits.Select(t => t.defName).Order(StringComparer.Ordinal).SequenceEqual(item.UniqueWeaponTraits.Order(StringComparer.Ordinal))) return false;
        }
        return true;
    }
}
