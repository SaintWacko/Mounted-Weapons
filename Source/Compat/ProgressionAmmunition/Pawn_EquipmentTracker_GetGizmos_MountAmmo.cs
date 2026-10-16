using System.Collections.Generic;
using HarmonyLib;
using ProgressionAmmunition;
using RimWorld;
using Verse;

namespace MountedWeapons.Ammo
{
    /// <summary>
    /// A pawn manning a mount shows the mount's ammo bar among their own gizmos, in place of
    /// the one for the weapon in their hands. Progression: Ammunition adds its gizmos for the
    /// equipped weapon in a postfix on this method (Pawn_EquipmentTracker_GetGizmos_Patch);
    /// this runs after it, drops that weapon's ammo bar and its "refill with consumable"
    /// button, and adds Gizmo_MountAmmo for the mount. Perspective Shift's own gizmo panel is
    /// built from pawn.GetGizmos(), so the inhabited gunner gets the same swap.
    ///
    /// Shown whenever the pawn mans the mount, drafted or not: P:A's "show only drafted"
    /// setting is for a pawn's own weapon, and a gunner is firing the mount either way.
    /// P:A's dev-mode buttons for the pawn's weapon are left alone.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.GetGizmos))]
    [HarmonyAfter("ProgressionAmmunitionMod")]
    public static class Pawn_EquipmentTracker_GetGizmos_MountAmmo
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn_EquipmentTracker __instance)
        {
            Building_WeaponMount mount = AvatarMountControl.ControlledMount(__instance.pawn);
            // Player mounts only, as on the mount itself: the bar's marker is a player setting.
            if (mount == null || mount.Faction != Faction.OfPlayer || !MountAmmo.UsesAmmo(mount, out CompAmmo mountComp))
            {
                foreach (Gizmo g in __result) yield return g;
                yield break;
            }

            ThingDef consumable = __instance.Primary?.TryGetComp<CompAmmo>()?.ConsumableDef;
            string refillLabel = consumable != null ? (string)"PA_RefillWithConsumable".Translate(consumable.label) : null;
            foreach (Gizmo g in __result)
            {
                if (g is Gizmo_Ammo) continue;
                if (refillLabel != null && g is Command_Action cmd && cmd.defaultLabel == refillLabel) continue;
                yield return g;
            }
            yield return new Gizmo_MountAmmo(mountComp);
        }
    }
}
