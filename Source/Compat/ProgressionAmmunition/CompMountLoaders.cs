using System.Collections.Generic;
using System.Linq;
using ProgressionAmmunition;
using RimWorld;
using Verse;

namespace MountedWeapons.Ammo
{
    public class CompProperties_MountLoaders : CompProperties_AssignableToPawn
    {
        public CompProperties_MountLoaders()
        {
            compClass = typeof(CompMountLoaders);
        }
    }

    /// <summary>
    /// Pawns assigned to keep this mount loaded, through vanilla's assign window (the one
    /// beds and thrones use). Also registers the mount with the map, so the work giver
    /// scans mounts only, not every building.
    /// </summary>
    public class CompMountLoaders : CompAssignableToPawn
    {
        public override IEnumerable<Pawn> AssigningCandidates
        {
            get
            {
                if (!parent.Spawned) return Enumerable.Empty<Pawn>();
                // Colonists and colony mechs; animals don't run work givers, so they can't load.
                return parent.Map.mapPawns.FreeColonists.Concat(parent.Map.mapPawns.SpawnedColonyMechs);
            }
        }

        public override AcceptanceReport CanAssignTo(Pawn pawn)
        {
            if (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
            {
                return "MW_PA_LoaderCannotHaul".Translate(pawn.Named("PAWN"));
            }
            return AcceptanceReport.WasAccepted;
        }

        protected override bool ShouldShowAssignmentGizmo()
        {
            return parent.Faction == Faction.OfPlayer && ProgressionAmmunitionMod.Enabled;
        }

        protected override string GetAssignmentGizmoLabel() => "MW_PA_AssignLoaders".Translate();

        protected override string GetAssignmentGizmoDesc() => "MW_PA_AssignLoadersDesc".Translate();

        // Loaders come back when a mount is reinstalled from a minified item.
        protected override bool CanSetUninstallAssignedPawn(Pawn pawn)
        {
            return pawn != null && !pawn.Dead && pawn.Spawned && pawn.Map == parent.Map;
        }

        /// <summary>
        /// The current loaders. Vanilla only clears an assignment for beds and the like (via
        /// Pawn_Ownership), so a loader who died, left or changed faction is dropped here,
        /// on read. A backwards loop, since this runs inside the work giver's ShouldSkip.
        /// </summary>
        public List<Pawn> Loaders
        {
            get
            {
                for (int i = assignedPawns.Count - 1; i >= 0; i--)
                {
                    Pawn p = assignedPawns[i];
                    if (p == null || p.Dead || p.Destroyed || p.Faction != parent.Faction)
                    {
                        assignedPawns.RemoveAt(i);
                    }
                }
                return assignedPawns;
            }
        }

        public override string CompInspectStringExtra()
        {
            if (!ProgressionAmmunitionMod.Enabled || Loaders.Count == 0) return null;
            return "MW_PA_InspectLoaders".Translate(assignedPawns.Select(p => p.LabelShort).ToCommaList());
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (parent is Building_WeaponMount mount)
            {
                MapComponent_MountAmmo.For(parent.Map)?.Register(mount);
            }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            if (parent is Building_WeaponMount mount)
            {
                MapComponent_MountAmmo.For(map)?.Deregister(mount);
            }
        }
    }

    public class MapComponent_MountAmmo : MapComponent
    {
        public readonly List<Building_WeaponMount> mounts = new List<Building_WeaponMount>();

        public MapComponent_MountAmmo(Map map) : base(map)
        {
        }

        public static MapComponent_MountAmmo For(Map map) => map?.GetComponent<MapComponent_MountAmmo>();

        public void Register(Building_WeaponMount mount)
        {
            if (!mounts.Contains(mount)) mounts.Add(mount);
        }

        public void Deregister(Building_WeaponMount mount)
        {
            mounts.Remove(mount);
        }
    }
}
