using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Profile;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldGenerationRuntime : IClientWorldGenerationRuntime
{
    public IClientWorldGenerationSession Bind() => new Session();
    private sealed class Session : IClientWorldGenerationSession
    {
        private readonly Game? game = Current.Game;
        private World? world = Current.Game?.World;
        public bool IsCurrent => ReferenceEquals(game, Current.Game) && ReferenceEquals(world, game?.World);
        public void Queue(Action generate) => LongEventHandler.QueueLongEvent(generate, "GeneratingWorld", doAsynchronously: true, null);
        public void WhenFinished(Action complete) => LongEventHandler.ExecuteWhenFinished(complete);
        public IClientGeneratedWorld Generate(ClientWorldGenerationInput input, Func<bool> current)
        {
            if (!current() || game?.InitData is null) throw new InvalidOperationException("World setup Game is unavailable.");
            var c = input.Configuration;
            var factions = c.FactionDefNames.Select(DefDatabase<FactionDef>.GetNamedSilentFail).Where(def => def is not null).ToList();
            if (factions.Count == 0) factions = DefDatabase<FactionDef>.AllDefsListForReading.ToList();
            float pollution = ClashOfRimCompatibilityApi.ResolveWorldGenerationFloatSetting(c, "pollution", 0f);
            if (float.IsNaN(pollution) || float.IsInfinity(pollution) || pollution < 0f)
                throw new ArgumentException("Invalid server generation pollution: " + pollution.ToString(CultureInfo.InvariantCulture));
            if (!current()) throw new OperationCanceledException("World setup was replaced.");
            var previousCreatingWorld = Current.CreatingWorld;
            game.InitData.ResetWorldRelatedMapInitData();
            Rand.EnsureStateStackEmpty(); Rand.PushState(GenText.StableStringHash(input.Seed));
            try
            {
                var generated = WorldGenerator.GenerateWorld(input.Coverage, input.Seed,
                    ParseEnum(c.OverallRainfall, OverallRainfall.Normal), ParseEnum(c.OverallTemperature, OverallTemperature.Normal),
                    ParseEnum(c.OverallPopulation, OverallPopulation.Normal), ParseEnum(c.LandmarkDensity, LandmarkDensity.Normal), factions, pollution);
                return new Generated(this, generated, world);
            }
            catch
            {
                // A failed native generation has no returned World to dispose. Never clear
                // another Game's CreatingWorld after an ownership change.
                if (current() && !ReferenceEquals(Current.CreatingWorld, previousCreatingWorld)) Current.CreatingWorld = null;
                throw;
            }
            finally { Rand.PopState(); }
        }
        private sealed class Generated : IClientGeneratedWorld
        {
            private readonly Session owner;
            private readonly World value;
            private readonly World? previous;
            private bool published, complete, disposed;
            internal Generated(Session owner, World value, World? previous) { this.owner = owner; this.value = value; this.previous = previous; }
            public void Publish()
            {
                if (!owner.IsCurrent) return;
                owner.game!.World = value; owner.world = value; published = true;
            }
            public ClientWorldFeatureNames? ApplySubstrate(WorldSubstratePackage substrate, ModWorldConfigurationDto configuration)
            {
                if (!WorldSubstrateRuntime.TryApply(substrate, configuration, out var names, out var failure))
                    throw new InvalidOperationException("Server world substrate could not be applied: " + failure);
                return names;
            }
            public void Complete(Func<bool> current)
            {
                if (!current()) return;
                ClientWorldConfigurationRuntime.RefreshWorldFeatureTexts();
                if (!current()) return;
                MemoryUtility.UnloadUnusedUnityAssets();
                if (!current()) return;
                value.renderer.RegenerateAllLayersNow();
                if (!current()) return;
                if (ReferenceEquals(Current.CreatingWorld, value)) Current.CreatingWorld = null;
                complete = true;
            }
            public void Dispose()
            {
                if (disposed) return; disposed = true;
                if (!complete && published && owner.IsCurrent) { owner.game!.World = previous; owner.world = previous; }
                if (ReferenceEquals(Current.CreatingWorld, value)) Current.CreatingWorld = null;
            }
        }
    }
    internal static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum
        => Enum.TryParse(value, true, out TEnum parsed) && Enum.IsDefined(typeof(TEnum), parsed) ? parsed : fallback;
}
