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

        // Not saved: after a load the gunner simply looks again on the next check.
        private readonly Dictionary<int, int> nextGunnerTry = new Dictionary<int, int>();

        // Mounts whose gunner has already been reported as having no ammo since the mount
        // ran dry, so the message shows once per empty magazine rather than every retry.
        // Cleared when rounds are loaded, the weapon comes off or the mount despawns.
        // Not saved, and emptied when a different game is running (ClearIfNewGame), so
        // the message can show once more after loading a save, and an ID repeated from
        // another save doesn't hide it.
        internal static readonly HashSet<int> reportedNoAmmo = new HashSet<int>();
        private static Game reportedFor;

        private static void ClearIfNewGame()
        {
            if (reportedFor == Current.Game) return;
            reportedFor = Current.Game;
            reportedNoAmmo.Clear();
        }

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
            return !ammo.CanBeFiredNow || CombatExtendedCompat.reloading.Contains(mount);
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
        /// </summary>
        public override void MountTick(Building_WeaponMount mount)
        {
            if (!mount.IsHashIntervalTick(GunnerCheckInterval)) return;
            CompAmmoUser ammo = AmmoOf(mount);
            if (ammo == null || !ammo.HasMagazine) return;
            ClearIfNewGame();
            if (ammo.CurMagCount > 0)
            {
                reportedNoAmmo.Remove(mount.thingIDNumber);
                return;
            }
            Pawn gunner = mount.Gunner;
            if (gunner == null || gunner.CurJobDef != JobDefOf.ManTurret || ModCompat.IsAvatar(gunner)) return;
            if (CombatExtendedCompat.reloading.Contains(mount)) return;

            int now = Find.TickManager.TicksGame;
            // The span check matters after loading an earlier save: thing IDs repeat but the
            // clock goes back, and a stored tick far in the future would block the gunner.
            if (nextGunnerTry.TryGetValue(mount.thingIDNumber, out int next) && now < next && next - now <= RetryTicks) return;
            nextGunnerTry[mount.thingIDNumber] = now + RetryTicks;
            mount.TryOrderReload();
            if (!reportedNoAmmo.Contains(mount.thingIDNumber) && !IsReloading(gunner, mount)
                && ReportNoAmmo(mount, gunner, MessageTypeDefOf.NegativeEvent))
            {
                reportedNoAmmo.Add(mount.thingIDNumber);
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
        /// next to the mount. This says so, and names the other types the gunner could use.
        /// Returns false, showing nothing, when the selected type is available and the
        /// refusal had some other cause (the ammo reserved, the mount burning).
        ///
        /// Only after a refusal, and at most once per empty magazine from MountTick, so the
        /// lister scan and the reach checks don't run while the mount is firing.
        /// </summary>
        internal static bool ReportNoAmmo(Building_WeaponMount mount, Pawn gunner, MessageTypeDef type)
        {
            if (mount.Faction != Faction.OfPlayer || gunner?.Map == null) return false;
            CompAmmoUser ammo = AmmoOf(mount);
            AmmoDef selected = ammo?.SelectedAmmo;
            if (ammo == null || !ammo.UseAmmo || selected == null) return false;
            if (AmmoAvailable(gunner, selected)) return false;

            List<string> others = new List<string>();
            if (ammo.CurAmmoSet?.ammoTypes != null)
            {
                foreach (AmmoLink link in ammo.CurAmmoSet.ammoTypes)
                {
                    AmmoDef def = link.ammo;
                    if (def == null || def == selected || !AmmoAvailable(gunner, def)) continue;
                    string label = def.ammoClass != null ? (string)def.ammoClass.LabelCap : (string)def.LabelCap;
                    if (!others.Contains(label)) others.Add(label);
                }
            }
            string text = others.Count == 0
                ? "MW_CE_NoAmmo".Translate(gunner.LabelShort, selected.label, mount.Label)
                : "MW_CE_NoAmmoOthers".Translate(gunner.LabelShort, selected.label, mount.Label, others.ToCommaList());
            Messages.Message(text, new LookTargets(mount), type, historical: false);
            return true;
        }

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
        /// start reloading and the cause is missing ammo, say so. A full magazine of the
        /// selected type is refused too, and needs no message.
        /// </summary>
        private static void ReportIfRefused(Building_WeaponMount mount)
        {
            Pawn gunner = mount.Gunner;
            if (gunner == null || IsReloading(gunner, mount)) return;
            ClearIfNewGame();
            if (ReportNoAmmo(mount, gunner, MessageTypeDefOf.RejectInput))
            {
                reportedNoAmmo.Add(mount.thingIDNumber);
            }
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

        public override string InspectLine(Building_WeaponMount mount)
        {
            CompAmmoUser ammo = AmmoOf(mount);
            if (ammo == null) return null;
            if (CombatExtendedCompat.reloading.Contains(mount)) return "CE_TurretReloading".Translate();
            if (!ammo.HasMagazine) return null;
            string count = ammo.CurMagCount + " / " + ammo.MagSize;
            AmmoDef current = ammo.CurrentAmmo;
            if (current == null) return "MW_CE_InspectMagazine".Translate(count);
            string type = current.ammoClass != null ? (string)current.ammoClass.LabelCap : (string)current.LabelCap;
            return "MW_CE_InspectMagazineAmmo".Translate(count, type);
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
