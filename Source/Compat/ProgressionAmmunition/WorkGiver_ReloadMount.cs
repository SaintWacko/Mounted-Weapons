using System.Collections.Generic;
using ProgressionAmmunition;
using RimWorld;
using Verse;
using Verse.AI;

namespace MountedWeapons.Ammo
{
    /// <summary>
    /// Hauling work: reload empty mounts, and loaders keep their own mounts topped up.
    /// Scans the map's registered mounts only; ShouldSkip makes it a few integer
    /// comparisons per mount when none of them need anything, which is most of the time.
    /// </summary>
    public class WorkGiver_ReloadMount : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        // Reloading during a raid is the point of the job.
        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            return MapComponent_MountAmmo.For(pawn.Map)?.mounts;
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            if (!ProgressionAmmunitionMod.Enabled) return true;
            MapComponent_MountAmmo registry = MapComponent_MountAmmo.For(pawn.Map);
            if (registry == null) return true;
            List<Building_WeaponMount> mounts = registry.mounts;
            for (int i = 0; i < mounts.Count; i++)
            {
                Building_WeaponMount mount = mounts[i];
                if (mount.Faction != pawn.Faction || !MountAmmo.UsesAmmo(mount, out CompAmmo comp)) continue;
                if (comp.CurAmmo >= comp.MaxAmmo) continue;
                if (forced || comp.IsOutOfAmmo || (MountAmmo.BelowThreshold(comp) && MountAmmo.HasLoaders(mount))) return false;
            }
            return true;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!(t is Building_WeaponMount mount) || mount.Faction != pawn.Faction) return false;
            if (!MountAmmo.UsesAmmo(mount, out CompAmmo comp) || !MountAmmo.WantsReloadFrom(pawn, mount, comp, forced)) return false;
            if (mount.IsForbidden(pawn) || mount.IsBurning()) return false;
            if (!pawn.CanReserve(mount, 1, -1, AmmoDefOf.MW_MountReload, forced)) return false;
            if (!MountAmmo.TryFindSource(pawn, comp, out _))
            {
                JobFailReason.Is("MW_PA_NoAmmoSource".Translate(comp.WeaponAmmoType.ToStringHuman()));
                return false;
            }
            return true;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Building_WeaponMount mount = (Building_WeaponMount)t;
            if (!MountAmmo.UsesAmmo(mount, out CompAmmo comp) || !MountAmmo.TryFindSource(pawn, comp, out Thing source)) return null;
            return MountAmmo.MakeReloadJob(mount, source);
        }
    }

    /// <summary>
    /// Right-click order for a drafted pawn. Drafted pawns don't take work, so without
    /// this a loader drafted for a fight could not reload anything. Undrafted pawns get
    /// "Prioritize reloading" from the work giver automatically.
    /// </summary>
    public class FloatMenuOptionProvider_ReloadMount : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => false;
        protected override bool Multiselect => false;
        protected override bool MechanoidCanDo => true;

        protected override FloatMenuOption GetSingleOptionFor(Thing clickedThing, FloatMenuContext context)
        {
            if (!(clickedThing is Building_WeaponMount mount)) return null;
            Pawn pawn = context.FirstSelectedPawn;
            if (pawn == null || mount.Faction != pawn.Faction) return null;
            if (!MountAmmo.UsesAmmo(mount, out CompAmmo comp) || comp.CurAmmo >= comp.MaxAmmo) return null;

            string label = "MW_PA_ReloadMount".Translate(mount.LabelShort);
            if (!pawn.CanReach(mount, PathEndMode.Touch, Danger.Deadly))
            {
                return new FloatMenuOption(label + ": " + "MW_PA_NoPath".Translate(), null);
            }
            if (!pawn.CanReserve(mount, 1, -1, AmmoDefOf.MW_MountReload))
            {
                return new FloatMenuOption(label + ": " + "MW_PA_AlreadyReloading".Translate(), null);
            }
            if (!MountAmmo.TryFindSource(pawn, comp, out Thing source))
            {
                return new FloatMenuOption(label + ": " + "MW_PA_NoAmmoSource".Translate(comp.WeaponAmmoType.ToStringHuman()), null);
            }
            return FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption(label,
                () => pawn.jobs.TryTakeOrderedJob(MountAmmo.MakeReloadJob(mount, source), JobTag.Misc)), pawn, mount);
        }
    }
}
