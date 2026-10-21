using System.Text;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using Verse;

namespace MountedWeapons.CE
{
    /// <summary>
    /// CE swaps the duty job givers for its own ManTurrets subclasses, which override
    /// TryGiveJob without calling base, so the vanilla postfix that keeps a pawn who can't
    /// fire the mounted weapon off the mount never runs. Same postfix, on CE's overrides.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_JobGiver_ManTurretsCE_TryGiveJob
    {
        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(JobGiver_ManTurretsNearSelfCE), nameof(JobGiver_ManTurretsNearSelfCE.TryGiveJob));
            yield return AccessTools.DeclaredMethod(typeof(JobGiver_ManTurretsNearPointCE), nameof(JobGiver_ManTurretsNearPointCE.TryGiveJob));
        }

        public static void Postfix(Pawn pawn, ref Verse.AI.Job __result)
        {
            Patch_JobGiver_ManTurrets_TryGiveJob.Postfix(pawn, ref __result);
        }
    }

    /// <summary>
    /// CE runs out of ammo by calling CompAmmoUser.TryStartReload from the verb (after the
    /// last shot, and when a cast starts on an empty magazine). For a turret that goes to
    /// TurretRegistry.TryOrderReload, which puts the gunner on a reload job. The pawn the
    /// player inhabits in Perspective Shift reloads only when the player clicks the mount
    /// (AvatarMountControl.Click), as without CE, so the automatic order is skipped for
    /// that gunner. The click goes through TryForceReload and is not affected.
    /// </summary>
    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.TryStartReload))]
    public static class Patch_CompAmmoUser_TryStartReload
    {
        public static bool Prefix(CompAmmoUser __instance)
        {
            return !(__instance.turret is Building_WeaponMount mount) || !mount.AvatarControlled;
        }
    }

    /// <summary>
    /// The hit-chance tooltip with a mount selected. Vanilla writes it for any
    /// Building_TurretGun from its own ShotReport, which knows nothing of CE's ballistics
    /// (it read "Shooter 100%, Weapon 100%"). CE writes its own readout only for its turret
    /// class and for pawns, and only when vanilla's text is empty. For a mount firing a CE
    /// weapon this writes CE's readout the way CE does for its turrets: sway (after the
    /// mount's stability), spread, lighting, weather, cover and the rest, for the gunner
    /// as the shooter. A mount firing a weapon CE didn't patch keeps vanilla's tooltip,
    /// which is right for it.
    ///
    /// A postfix that replaces the text, so other mods' prefixes on the method still run.
    /// Vanilla's text for a mount is never empty, so CE's own postfix leaves it alone.
    ///
    /// With the mount's gunner selected the readout is the same, headed with the gunner's
    /// name, and a target outside the firing arc reads as one the gunner can't hit. The main
    /// assembly's gunner tooltip (Patch_TooltipUtility_ShotCalculationTipString_Gunner) runs
    /// earlier and is replaced here.
    /// </summary>
    [HarmonyPatch(typeof(TooltipUtility), nameof(TooltipUtility.ShotCalculationTipString))]
    public static class Patch_ShotCalculationTipString
    {
        public static void Postfix(Thing target, ref string __result)
        {
            Thing selected = Find.Selector.SingleSelectedThing;
            Pawn gunner = selected as Pawn;
            Building_WeaponMount mount = selected as Building_WeaponMount ?? GunnerUtility.ArmedMountOf(gunner);
            if (mount == null || mount == target || selected == target) return;
            if (!(mount.AttackVerb is Verb_LaunchProjectileCE verb)) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine();
            sb.Append("ShotBy".Translate(selected.LabelShort, selected).Resolve() + ":\n");
            string report = null;
            if ((gunner == null || mount.InArc(target)) && verb.CanHitTarget(new LocalTargetInfo(target), out report))
            {
                sb.Append(verb.ShiftVecReportFor(new LocalTargetInfo(target)).GetTextReadout());
            }
            else
            {
                sb.Append("CannotHit".Translate().Resolve());
                if (!report.NullOrEmpty()) sb.Append(" " + report + ".");
            }
            if (target is Pawn pawn && pawn.Faction == null && !pawn.InAggroMentalState && pawn.AnimalOrWildMan())
            {
                float chance = PawnUtility.GetManhunterOnDamageChance(pawn, mount);
                if (chance > 0f)
                {
                    sb.AppendLine();
                    sb.AppendLine(string.Format("{0}: {1}", "ManhunterPerHit".Translate(), chance.ToStringPercent()));
                }
            }
            __result = sb.ToString();
        }
    }
}
