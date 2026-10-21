using System.Collections.Generic;
using CombatExtended;
using HarmonyLib;
using Verse;

namespace MountedWeapons.CE
{
    /// <summary>
    /// CE adds a pawn's weapon gizmos (ammo, reload, fire mode, aim mode, and comps such as
    /// underbarrels) through CompPawnGizmo on the pawn, outside Pawn_EquipmentTracker. For a
    /// gunner, those for the weapon in hand are replaced by the mounted weapon's, the same
    /// set the mount shows (CEMountAmmoHandler.Gizmos). A pawn with more than one
    /// CompPawnGizmo (a bad patch, which CE itself warns about) gets them from the first only.
    /// </summary>
    [HarmonyPatch(typeof(CompPawnGizmo), nameof(CompPawnGizmo.CompGetGizmosExtra))]
    public static class Patch_CompPawnGizmo_CompGetGizmosExtra
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, CompPawnGizmo __instance)
        {
            Pawn pawn = __instance.parent as Pawn;
            Building_WeaponMount mount = GunnerUtility.ArmedMountOf(pawn);
            if (mount == null)
            {
                foreach (Gizmo gizmo in __result) yield return gizmo;
                yield break;
            }
            if (pawn.GetComp<CompPawnGizmo>() != __instance || MountAmmoHandler.Active == null) yield break;
            foreach (Gizmo gizmo in MountAmmoHandler.Active.Gizmos(mount))
            {
                yield return gizmo;
            }
        }
    }
}
