using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProgressionAmmunition;
using RimWorld;
using Verse;
using Verse.AI;

namespace MountedWeapons.Ammo
{
    [DefOf]
    public static class AmmoDefOf
    {
        public static JobDef MW_ReloadMount;

        /// <summary>
        /// The mount is reserved on its own layer while someone reloads it. A gunner holds
        /// the default-layer reservation for as long as they man the mount, so a loader
        /// can still top it up mid-fight, but two pawns never fetch ammo for the same one.
        /// </summary>
        public static ReservationLayerDef MW_MountReload;

        static AmmoDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(AmmoDefOf));
        }
    }

    [StaticConstructorOnStartup]
    public static class AmmoCompatStartup
    {
        static AmmoCompatStartup()
        {
            MountAmmoHandler.Active = new ProgressionMountAmmoHandler();
            new Harmony(MountedWeaponsMod.HarmonyId + ".ProgressionAmmunition").PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    /// <summary>
    /// Everything about where a mount's ammo comes from. Progression: Ammunition keeps the
    /// count on the weapon (CompAmmo), so it travels with the weapon on and off the mount.
    /// </summary>
    public static class MountAmmo
    {
        private static List<ThingDef> stationDefs;

        /// <summary>
        /// Whether this mount counts shots. Follows Progression: Ammunition's own rule for
        /// pawns: off when that mod is disabled, and off for anything not the player's
        /// while "only colonists use ammo" is on, since nobody would reload it.
        /// </summary>
        public static bool UsesAmmo(Building_WeaponMount mount, out CompAmmo comp)
        {
            comp = null;
            if (!ProgressionAmmunitionMod.Enabled || !mount.HasWeapon) return false;
            if (mount.Faction != Faction.OfPlayer
                && (ProgressionAmmunitionMod.settings == null || ProgressionAmmunitionMod.settings.onlyColonistsUseAmmo)) return false;
            comp = mount.gun.TryGetComp<CompAmmo>();
            return comp != null;
        }

        /// <summary>An empty automated mount: the general hauling job.</summary>
        public static bool OpenToAnyHauler(Building_WeaponMount mount, CompAmmo comp)
        {
            return comp.IsOutOfAmmo && !mount.IsManned;
        }

        public static bool OutOfAmmo(Building_WeaponMount mount)
        {
            return UsesAmmo(mount, out CompAmmo comp) && comp.IsOutOfAmmo;
        }

        /// <summary>
        /// At or under the threshold the player set on the ammo bar. The bar starts at
        /// 100%, which means "anything below full", as Progression: Ammunition does for pawns.
        /// </summary>
        public static bool BelowThreshold(CompAmmo comp)
        {
            int max = comp.MaxAmmo;
            return comp.CurAmmo < max && (float)comp.CurAmmo / max <= comp.autoReloadThreshold;
        }

        public static bool IsLoader(Pawn pawn, Building_WeaponMount mount)
        {
            CompMountLoaders loaders = mount.GetComp<CompMountLoaders>();
            return loaders != null && loaders.Loaders.Contains(pawn);
        }

        public static bool HasLoaders(Building_WeaponMount mount)
        {
            CompMountLoaders loaders = mount.GetComp<CompMountLoaders>();
            return loaders != null && loaders.Loaders.Count > 0;
        }

        /// <summary>
        /// Anyone hauling reloads an empty automated mount. An empty manned mount is left to
        /// its loaders and its gunner (who reloads from MountTick): the gunner is standing
        /// next to it, while a random hauler might be across the map. A loader also tops up
        /// their own mounts at the threshold. A player order (forced) reloads anything below full.
        /// </summary>
        public static bool WantsReloadFrom(Pawn pawn, Building_WeaponMount mount, CompAmmo comp, bool forced)
        {
            if (comp.CurAmmo >= comp.MaxAmmo) return false;
            if (forced || OpenToAnyHauler(mount, comp)) return true;
            return BelowThreshold(comp) && IsLoader(pawn, mount);
        }

        /// <summary>
        /// Where this pawn gets ammo, in the player's order of preference: the nearest
        /// station of the right type (unlimited by default), then a pack the pawn is
        /// carrying, then the nearest pack in storage.
        /// </summary>
        public static bool TryFindSource(Pawn pawn, CompAmmo comp, out Thing source)
        {
            source = ClosestStation(pawn, comp);
            if (source != null) return true;

            ThingDef pack = comp.ConsumableDef;
            if (pack == null) return false;

            source = CarriedPack(pawn, pack);
            if (source != null) return true;

            source = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForDef(pack),
                PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f,
                t => !t.IsForbidden(pawn) && pawn.CanReserve(t, 10, 1));
            return source != null;
        }

        public static Thing CarriedPack(Pawn pawn, ThingDef pack)
        {
            ThingOwner<Thing> inv = pawn.inventory?.innerContainer;
            if (inv == null) return null;
            for (int i = 0; i < inv.Count; i++)
            {
                if (inv[i].def == pack) return inv[i];
            }
            return null;
        }

        private static Building_AmmoRecharger ClosestStation(Pawn pawn, CompAmmo comp)
        {
            if (stationDefs == null)
            {
                stationDefs = new List<ThingDef>();
                foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (def.thingClass != null && typeof(Building_AmmoRecharger).IsAssignableFrom(def.thingClass))
                    {
                        stationDefs.Add(def);
                    }
                }
            }
            Building_AmmoRecharger best = null;
            int bestDist = int.MaxValue;
            for (int d = 0; d < stationDefs.Count; d++)
            {
                // listerThings keeps a list per def, so this touches stations only.
                List<Thing> stations = pawn.Map.listerThings.ThingsOfDef(stationDefs[d]);
                for (int i = 0; i < stations.Count; i++)
                {
                    if (!(stations[i] is Building_AmmoRecharger station) || station.Faction != pawn.Faction) continue;
                    int dist = (station.Position - pawn.Position).LengthHorizontalSquared;
                    if (dist >= bestDist) continue;
                    if (!station.CanRecharge(comp) || station.IsForbidden(pawn) || !pawn.CanReserve(station)) continue;
                    if (!pawn.CanReach(station, PathEndMode.Touch, Danger.Deadly)) continue;
                    best = station;
                    bestDist = dist;
                }
            }
            return best;
        }

        public static Job MakeReloadJob(Building_WeaponMount mount, Thing source)
        {
            Job job = JobMaker.MakeJob(AmmoDefOf.MW_ReloadMount, mount, source);
            job.count = 1;
            return job;
        }
    }
}
