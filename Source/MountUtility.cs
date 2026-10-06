using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace MountedWeapons
{
    public static class MountUtility
    {
        // Def-level answers never change during a session, so they are cached.
        private static readonly Dictionary<ThingDef, bool> mountableCache = new Dictionary<ThingDef, bool>();
        private static ThingCategoryDef grenadesCategory;
        private static bool grenadesSearched;

        /// <summary>
        /// Whether this kind of weapon can ever go on a mount: a ranged primary weapon
        /// that fires projectiles or a beam, not a single-use launcher, not a grenade,
        /// and not a turret's built-in gun.
        /// </summary>
        public static bool IsMountableDef(ThingDef def)
        {
            if (def == null) return false;
            if (mountableCache.TryGetValue(def, out bool cached)) return cached;
            bool result = ComputeMountable(def);
            mountableCache[def] = result;
            return result;
        }

        private static bool ComputeMountable(ThingDef def)
        {
            if (!def.IsRangedWeapon || def.equipmentType != EquipmentType.Primary) return false;
            if (!def.HasComp(typeof(CompEquippable))) return false;
            if (def.destroyOnDrop) return false; // turret guns, mech-only built-ins
            if (def.weaponTags != null && def.weaponTags.Contains("TurretGun")) return false;

            if (!grenadesSearched)
            {
                grenadesCategory = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("Grenades");
                grenadesSearched = true;
            }
            if (grenadesCategory != null && def.IsWithinCategory(grenadesCategory)) return false;

            List<VerbProperties> verbs = def.Verbs;
            if (verbs.NullOrEmpty()) return false;
            VerbProperties primary = verbs.FirstOrDefault(v => v.isPrimary) ?? verbs[0];
            Type verbClass = primary.verbClass;
            if (verbClass == null || primary.range <= 0f) return false;
            if (typeof(Verb_ShootOneUse).IsAssignableFrom(verbClass)) return false;
            return typeof(Verb_LaunchProjectile).IsAssignableFrom(verbClass)
                || typeof(Verb_ShootBeam).IsAssignableFrom(verbClass);
        }

        /// <summary>Whether this particular weapon can go on a mount right now.</summary>
        public static bool CanMount(Thing weapon, out string reason)
        {
            reason = null;
            if (weapon == null || !IsMountableDef(weapon.def))
            {
                reason = "MW_ReasonNotMountable".Translate();
                return false;
            }
            CompBiocodable bio = weapon.TryGetComp<CompBiocodable>();
            if (bio != null && bio.Biocoded)
            {
                reason = "MW_ReasonBiocoded".Translate();
                return false;
            }
            return true;
        }
    }
}
