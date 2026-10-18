using System;
using System.Collections.Generic;
using CombatExtended;
using CombatExtended.Compatibility;
using RimWorld;
using Verse;
using Verse.AI;

namespace MountedWeapons.CE
{
    /// <summary>
    /// Combat Extended's ammo on a mount. The magazine lives on the installed weapon's
    /// CompAmmoUser, as it does in a pawn's hands, so it stays with the weapon on and off
    /// the mount. CE's own reload job (JobDriver_ReloadTurret) does the loading: it reaches
    /// the mount through TurretRegistry, which CombatExtendedCompat registers.
    ///
    /// Progression: Ammunition stands down under CE (LoadFolders.xml), so this is the only
    /// MountAmmoHandler loaded when CE is active.
    /// </summary>
    public class CEMountAmmoHandler : MountAmmoHandler
    {
        private const int GunnerCheckInterval = 30;
        private const int RetryTicks = 250;

        public static CompAmmoUser AmmoOf(Building_WeaponMount mount) => mount.MountedWeapon?.TryGetComp<CompAmmoUser>();

        /// <summary>
        /// Empty, or being reloaded. Without this gate the mount kept finding targets and
        /// aiming at them, and CE's reload check on Verb.TryStartCastOn refused every shot
        /// silently, so an empty mount aimed over and over (run #13). CE's own turret does
        /// not fire while reloading either. CE weapons that take ammo but have no magazine
        /// can't be mounted (CEMountCombatHandler.IsMountableVerb).
        /// </summary>
        public override bool OutOfAmmo(Building_WeaponMount mount)
        {
            CompAmmoUser ammo = AmmoOf(mount);
            if (ammo == null) return false;
            return !ammo.CanBeFiredNow || CEMountState.IsReloading(mount);
        }

        /// <summary>
        /// A gunner whose mount runs dry goes for ammo and comes back to keep manning, as
        /// CE's manned turrets do. TurretRegistry.TryOrderReload (not forced) acts only on
        /// an empty magazine and starts CE's reload job with the manning job resumed after.
        /// Haulers can also reload any mount through CE's own work giver, which finds it
        /// through the map's TurretTracker.
        ///
        /// Not for Perspective Shift's inhabited pawn: the player is steering them, so they
        /// reload when the player clicks the mount (OrderReload).
        ///
        /// A refused order is reported when the cause is missing ammo. The message repeats
        /// only when its text changes (another gunner, other types now on the map), so a
        /// gunner left waiting doesn't post it every retry, but spawning a different ammo
        /// type or swapping the gunner tells the player again (run #2: a second manning
        /// after HE was spawned showed nothing).
        /// </summary>
        public override void MountTick(Building_WeaponMount mount)
        {
            if (!mount.IsHashIntervalTick(GunnerCheckInterval)) return;
            CompAmmoUser ammo = AmmoOf(mount);
            if (ammo == null || !ammo.HasMagazine) return;
            if (ammo.CurMagCount > 0)
            {
                CEMountState.ClearReport(mount);
                return;
            }
            Pawn gunner = mount.Gunner;
            if (gunner == null || gunner.CurJobDef != JobDefOf.ManTurret || ModCompat.IsAvatar(gunner)) return;
            if (CEMountState.IsReloading(mount)) return;
            if (!CEMountState.RetryDue(mount, RetryTicks)) return;

            mount.TryOrderReload();
            if (IsReloading(gunner, mount)) return;
            string text = NoAmmoText(mount, gunner);
            if (text != null && CEMountState.ShouldReport(mount, text))
            {
                Messages.Message(text, new LookTargets(mount), MessageTypeDefOf.NegativeEvent, historical: false);
            }
        }

        /// <summary>
        /// The reload order starts CE's job synchronously, so right after it the gunner is
        /// either on that job or the order was refused.
        /// </summary>
        private static bool IsReloading(Pawn pawn, Building_WeaponMount mount) =>
            pawn.CurJobDef == CE_JobDefOf.ReloadTurret && pawn.CurJob.targetA.Thing == mount;

        /// <summary>
        /// CE refuses a reload without a word when none of the selected ammo type is on the
        /// map (CE_NoAmmoAvailable only reaches a float menu tooltip), and the selected type
        /// is what the gunner fetches, even when another type in the weapon's set is lying
        /// next to the mount. This is the message saying so, naming the other types the
        /// gunner could use and how to pick one. Null when the selected type is available
        /// and the refusal had some other cause (the ammo reserved, the mount burning).
        ///
        /// Only built after a refusal, at most once per retry from MountTick, so the lister
        /// scan and the reach checks don't run while the mount is firing.
        /// </summary>
        private static string NoAmmoText(Building_WeaponMount mount, Pawn gunner)
        {
            if (mount.Faction != Faction.OfPlayer || gunner?.Map == null) return null;
            CompAmmoUser ammo = AmmoOf(mount);
            AmmoDef selected = ammo?.SelectedAmmo;
            if (ammo == null || !ammo.UseAmmo || selected == null) return null;
            if (AmmoAvailable(gunner, selected)) return null;

            List<string> others = new List<string>();
            if (ammo.CurAmmoSet?.ammoTypes != null)
            {
                foreach (AmmoLink link in ammo.CurAmmoSet.ammoTypes)
                {
                    AmmoDef def = link.ammo;
                    if (def == null || def == selected || !AmmoAvailable(gunner, def)) continue;
                    string label = TypeLabel(def);
                    if (!others.Contains(label)) others.Add(label);
                }
            }
            if (others.Count == 0)
            {
                return "MW_CE_NoAmmo".Translate(gunner.LabelShort, selected.label, mount.Label);
            }
            // With CE's right-click ammo select on (its default), a left-click on a manned
            // mount's reload button reloads the selected type again, and the type menu is on
            // right-click; the hint has to say which (run #2).
            string hint = Controller.settings.RightClickAmmoSelect
                ? "MW_CE_ChangeAmmoRightClick".Translate()
                : "MW_CE_ChangeAmmoClick".Translate();
            return "MW_CE_NoAmmoOthers".Translate(gunner.LabelShort, selected.label, mount.Label, others.ToCommaList(), hint);
        }

        private static string TypeLabel(AmmoDef def) =>
            def.ammoClass != null ? (string)def.ammoClass.LabelCap : (string)def.LabelCap;

        /// <summary>
        /// Roughly CE's own ammo search for a turret reload: spawned on the map, not
        /// forbidden, not burning or cooking off, reservable and reachable. CE does not take
        /// ammo from the gunner's inventory for a turret, so neither does this.
        /// </summary>
        private static bool AmmoAvailable(Pawn pawn, AmmoDef def)
        {
            List<Thing> things = pawn.Map.listerThings.ThingsOfDef(def);
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing is AmmoThing cooking && cooking.IsCookingOff) continue;
                if (thing.IsBurning() || thing.IsForbidden(pawn)) continue;
                if (!pawn.CanReserve(thing) || !pawn.CanReach(thing, PathEndMode.ClosestTouch, Danger.Deadly)) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// After a reload button press or a Perspective Shift click: if the gunner didn't
        /// start reloading and the cause is missing ammo, say so, every time, since the
        /// player just asked. A full magazine of the selected type is refused too, and
        /// needs no message.
        /// </summary>
        private static void ReportIfRefused(Building_WeaponMount mount)
        {
            Pawn gunner = mount.Gunner;
            if (gunner == null || IsReloading(gunner, mount)) return;
            string text = NoAmmoText(mount, gunner);
            if (text == null) return;
            CEMountState.ShouldReport(mount, text);
            Messages.Message(text, new LookTargets(mount), MessageTypeDefOf.RejectInput, historical: false);
        }

        /// <summary>
        /// Perspective Shift's inhabited gunner clicked the mount they man. Forced, so a
        /// part-empty magazine or a change of ammo type is reloaded too, as CE's reload
        /// button does.
        /// </summary>
        public override void OrderReload(Pawn pawn, Building_WeaponMount mount)
        {
            if (pawn.CurJobDef != JobDefOf.ManTurret || pawn.CurJob.targetA.Thing != mount) return;
            if (AmmoOf(mount) == null) return;
            mount.TryForceReload();
            ReportIfRefused(mount);
        }

        /// <summary>
        /// The loaded type, as CE's magazine bar shows it, plus the type picked on the reload
        /// button when that differs: CE only swaps the magazine at the next reload, so
        /// without it the panel kept saying FMJ after AP was picked (run #2).
        /// </summary>
        public override string InspectLine(Building_WeaponMount mount)
        {
            CompAmmoUser ammo = AmmoOf(mount);
            if (ammo == null) return null;
            if (CEMountState.IsReloading(mount)) return "CE_TurretReloading".Translate();
            if (!ammo.HasMagazine) return null;
            string count = ammo.CurMagCount + " / " + ammo.MagSize;
            AmmoDef current = ammo.CurrentAmmo;
            AmmoDef selected = ammo.UseAmmo ? ammo.SelectedAmmo : null;
            if (current == null)
            {
                return selected == null
                    ? "MW_CE_InspectMagazine".Translate(count)
                    : "MW_CE_InspectMagazineAmmo".Translate(count, TypeLabel(selected));
            }
            if (selected != null && selected != current)
            {
                return "MW_CE_InspectMagazineNext".Translate(count, TypeLabel(current), TypeLabel(selected));
            }
            return "MW_CE_InspectMagazineAmmo".Translate(count, TypeLabel(current));
        }

        /// <summary>
        /// The weapon's own CE gizmos, as Building_TurretGunCE shows them: the magazine bar,
        /// reload and ammo type (CompAmmoUser), then fire and aim mode (CompFireModes). The
        /// mount only asks for these on a player mount.
        ///
        /// CE gives the reload button an action on any mannable turret, but its button only
        /// runs it while someone mans the mount; on an unmanned mount it opens the ammo type
        /// menu, and haulers do the reload. The action is wrapped so a refused reload
        /// reports missing ammo. CE's ammo menu reads the
        /// command's action when it opens, so its Reload entry runs the wrapped one too.
        /// </summary>
        public override IEnumerable<Gizmo> Gizmos(Building_WeaponMount mount)
        {
            Thing weapon = mount.MountedWeapon;
            if (weapon == null) yield break;
            CompAmmoUser ammo = weapon.TryGetComp<CompAmmoUser>();
            if (ammo != null)
            {
                foreach (Gizmo gizmo in ammo.CompGetGizmosExtra())
                {
                    if (gizmo is Command_Reload reload && reload.action != null)
                    {
                        Action original = reload.action;
                        reload.action = () =>
                        {
                            original();
                            ReportIfRefused(mount);
                        };
                    }
                    yield return gizmo;
                }
            }
            CompFireModes modes = weapon.TryGetComp<CompFireModes>();
            if (modes != null)
            {
                foreach (Command command in modes.GenerateGizmos())
                {
                    yield return command;
                }
            }
        }
    }
}
