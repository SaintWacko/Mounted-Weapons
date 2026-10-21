using RimWorld;
using UnityEngine;
using Verse;

namespace MountedWeapons.CE
{
    /// <summary>
    /// Multiplies a weapon's magazine size while it sits on a mount, by the mount's
    /// ammoCapacityFactor. Added to Combat Extended's MagazineCapacity stat by
    /// Compat/CombatExtended/1.6/Patches/MW_MagazineCapacity.xml. CE reads the magazine size
    /// through that stat everywhere (CompAmmoUser.MagSize: reloads, the magazine bar, how
    /// much ammo a hauler brings), so this one part covers all of them.
    ///
    /// CE caches the stat for 250 ticks; CEMountAmmoHandler clears the weapon's entry when
    /// it goes on or comes off a mount, or the setting changes.
    /// </summary>
    public class StatPart_MountMagazine : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
            Building_WeaponMount mount = MountOf(req);
            if (mount == null || val <= 0f) return;
            val = Mathf.Max(1f, Mathf.Round(val * mount.Ext.ammoCapacityFactor));
        }

        public override string ExplanationPart(StatRequest req)
        {
            Building_WeaponMount mount = MountOf(req);
            if (mount == null || mount.Ext.ammoCapacityFactor == 1f) return null;
            return "MW_CE_StatMountMagazine".Translate(mount.LabelCap, mount.Ext.ammoCapacityFactor.ToString("0.#"));
        }

        private static Building_WeaponMount MountOf(StatRequest req) =>
            req.HasThing ? Building_WeaponMount.HoldingMount(req.Thing) : null;
    }
}
