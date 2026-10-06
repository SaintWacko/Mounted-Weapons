using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace MountedWeapons
{
    /// <summary>Hauling work: carry a weapon the player picked to its mount and install it.</summary>
    public class WorkGiver_InstallMountedWeapon : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            return pawn.Map.listerBuildings.AllBuildingsColonistOfClass<Building_WeaponMount>()
                .Where(m => m.pendingWeapon != null);
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return !pawn.Map.listerBuildings.AllBuildingsColonistOfClass<Building_WeaponMount>()
                .Any(m => m.pendingWeapon != null);
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!(t is Building_WeaponMount mount) || mount.pendingWeapon == null) return false;
            Thing weapon = mount.pendingWeapon;
            if (!weapon.Spawned || !mount.PendingWeaponStillValid()) return false;
            if (mount.IsForbidden(pawn) || mount.IsBurning()) return false;
            if (weapon.IsForbidden(pawn)) return false;
            if (!pawn.CanReserveAndReach(weapon, PathEndMode.ClosestTouch, Danger.Deadly, 1, -1, null, forced)) return false;
            return MountUtility.CanMount(weapon, out _);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Building_WeaponMount mount = (Building_WeaponMount)t;
            Job job = JobMaker.MakeJob(MWDefOf.MW_InstallMountedWeapon, mount, mount.pendingWeapon);
            job.count = 1;
            return job;
        }
    }
}
