using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MountedWeapons
{
    /// <summary>
    /// While a pawn mans a mount that holds a weapon, the pawn is shown as if holding that
    /// weapon: the fire button, the fire-at-will toggle, the multi-select checks, squad
    /// attack, the hit-chance tooltip and the weapon under the colonist bar portrait all use
    /// the mount's weapon. Orders go to the mount, so the gunner never leaves it to carry
    /// one out. Ammo gizmos come from the ammo mods' own compat assemblies, each at the point
    /// where that mod adds them (Progression: Ammunition on Pawn_EquipmentTracker.GetGizmos,
    /// Combat Extended on CompPawnGizmo).
    /// </summary>
    public static class GunnerUtility
    {
        /// <summary>
        /// The player's mount this pawn is manning right now, with a weapon on it, or null.
        /// "Right now" is CompMannable's own test, so a pawn still walking to the mount keeps
        /// their own gizmos until they arrive.
        /// </summary>
        public static Building_WeaponMount ArmedMountOf(Pawn pawn)
        {
            Building_WeaponMount mount = AvatarMountControl.ControlledMount(pawn);
            if (mount == null || mount.Faction != Faction.OfPlayer || mount.Gunner != pawn) return null;
            return mount;
        }

        /// <summary>The weapon the pawn counts as holding: the mount's while manning one.</summary>
        public static ThingWithComps EffectivePrimary(Pawn_EquipmentTracker eq)
        {
            if (eq == null) return null;
            return ArmedMountOf(eq.pawn)?.MountedWeapon as ThingWithComps ?? eq.Primary;
        }

        public static CompEquippable EffectivePrimaryEq(Pawn_EquipmentTracker eq)
        {
            if (eq == null) return null;
            CompEquippable mounted = (ArmedMountOf(eq.pawn)?.MountedWeapon as ThingWithComps)?.GetComp<CompEquippable>();
            return mounted ?? eq.PrimaryEq;
        }

        /// <summary>
        /// The colonist bar draws the weapon under the portrait while drafted (the default
        /// setting). A gunner is armed whether drafted or not, so manning counts.
        /// </summary>
        public static bool PortraitDrafted(Pawn pawn) => pawn.Drafted || ArmedMountOf(pawn) != null;

        /// <summary>
        /// Same test as the vanilla CanOrderPlayerPawn in PawnAttackGizmoUtility, which is private.
        /// </summary>
        public static bool CanOrder(Pawn pawn) =>
            pawn.IsColonistPlayerControlled || pawn.IsColonyMechPlayerControlled || pawn.IsColonySubhumanPlayerControlled;

        public static Command_GunnerFire FireCommand(Building_WeaponMount mount)
        {
            Thing weapon = mount.MountedWeapon;
            Command_GunnerFire command = new Command_GunnerFire
            {
                mount = mount,
                ownerThing = weapon,
                verb = mount.AttackVerb,
                defaultDesc = weapon.LabelCap + ": " + weapon.def.description.CapitalizeFirst()
                    + "\n\n" + "MW_GunnerFireDesc".Translate(mount.LabelShort),
                tutorTag = "VerbTarget",
                hotKey = KeyBindingDefOf.Misc1,
                // The mount's own force-attack command does the same: an empty magazine
                // or a cooldown shouldn't stop the player choosing the next target.
                requiresAvailableVerb = false
            };
            Verb verb = mount.AttackVerb;
            if (verb.ProjectileFliesOverhead() && mount.Position.Roofed(mount.Map))
            {
                command.Disable("CannotFire".Translate() + ": " + "Roofed".Translate().CapitalizeFirst());
            }
            else
            {
                float cap = mount.Map.weatherManager.CurWeatherMaxRangeCap;
                if (cap > 0f && cap < verb.verbProps.minRange)
                {
                    command.Disable("CannotFire".Translate() + ": " + mount.Map.weatherManager.curWeather.LabelCap);
                }
            }
            return command;
        }

        public static Command_Toggle FireAtWillToggle(Building_WeaponMount mount)
        {
            return new Command_Toggle
            {
                hotKey = KeyBindingDefOf.Misc6,
                isActive = () => !mount.HoldFire,
                toggleAction = () => mount.HoldFire = !mount.HoldFire,
                icon = TexCommand.FireAtWill,
                defaultLabel = "CommandFireAtWillLabel".Translate(),
                defaultDesc = "CommandFireAtWillDesc".Translate(),
                // Perspective Shift hides the toggle for its avatar by this tag.
                tutorTag = "FireAtWillToggle"
            };
        }
    }

    /// <summary>
    /// The gunner's fire button. It shows the mounted weapon like a pawn's own fire button
    /// does, and targets with the mount's verb, so the targeter shows the mount's range and
    /// hit chance. The order goes to the mount (Building_WeaponMount.OrderAttack).
    ///
    /// It only groups with other gunners' buttons for the same weapon. When several are
    /// selected, the click reaches every button in the group: the first starts targeting and
    /// the rest join it (GunnerTargeting), and the order is passed to each of their mounts.
    /// </summary>
    public class Command_GunnerFire : Command_VerbTarget
    {
        public Building_WeaponMount mount;
        private readonly List<Building_WeaponMount> groupedMounts = new List<Building_WeaponMount>();

        public override bool GroupsWith(Gizmo other) =>
            other is Command_GunnerFire command && command.ownerThing?.def == ownerThing?.def;

        public override void MergeWith(Gizmo other)
        {
            base.MergeWith(other);
            if (other is Command_GunnerFire command)
            {
                groupedMounts.Add(command.mount);
                groupedMounts.AddRange(command.groupedMounts);
            }
        }

        public override void GizmoUpdateOnMouseover()
        {
            mount.DrawFireField();
            for (int i = 0; i < groupedMounts.Count; i++)
            {
                groupedMounts[i].DrawFireField();
            }
        }

        public override void ProcessInput(Event ev)
        {
            // Command_VerbTarget's version starts targeting with a pawn's verb in mind; the
            // caster here is the mount, which isn't selected.
            SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
            GunnerTargeting.Add(mount);
        }
    }

    /// <summary>
    /// One targeting session shared by every gunner's fire button in a multi-selection. The
    /// targeter takes one verb: the first mount's. Vanilla's targeter orders that mount
    /// through OrderAttack, and the mount passes the same target to the others here.
    /// </summary>
    public static class GunnerTargeting
    {
        private static Verb leaderVerb;
        private static readonly List<Building_WeaponMount> others = new List<Building_WeaponMount>();
        private static readonly List<Building_WeaponMount> tmp = new List<Building_WeaponMount>();

        private static bool SessionActive =>
            leaderVerb != null && Find.Targeter.IsTargeting && (object)Find.Targeter.targetingSource == leaderVerb;

        public static void Add(Building_WeaponMount mount)
        {
            if (SessionActive)
            {
                if (mount.AttackVerb != leaderVerb && !others.Contains(mount)) others.Add(mount);
                return;
            }
            Clear();
            leaderVerb = mount.AttackVerb;
            // The mount isn't selected (its gunner is), so the targeter must not stop for that.
            Find.Targeter.BeginTargeting(mount.AttackVerb, null, allowNonSelectedTargetingSource: true,
                null, Clear, requiresAvailableVerb: false);
        }

        public static void Clear()
        {
            leaderVerb = null;
            others.Clear();
        }

        /// <summary>Called from the mount's OrderAttack; does nothing outside a session it leads.</summary>
        public static void OrderOthers(Building_WeaponMount leader, LocalTargetInfo target)
        {
            if (!SessionActive || leader.AttackVerb != leaderVerb || others.Count == 0) return;
            tmp.Clear();
            tmp.AddRange(others);
            others.Clear();
            for (int i = 0; i < tmp.Count; i++)
            {
                Building_WeaponMount mount = tmp[i];
                if (mount.Spawned && mount.Map == leader.Map && mount.HasWeapon) mount.OrderAttack(target);
            }
            tmp.Clear();
        }
    }

    /// <summary>
    /// The pawn's weapon gizmos. For a gunner, everything vanilla builds here (the pawn's
    /// own fire buttons and the gizmos their weapon's comps add) is replaced by one fire
    /// button for the mounted weapon. Runs first among postfixes, so mods that add gizmos
    /// here in a postfix still see the result and add theirs.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.GetGizmos))]
    [HarmonyPriority(Priority.First)]
    public static class Patch_Pawn_EquipmentTracker_GetGizmos
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn_EquipmentTracker __instance)
        {
            Building_WeaponMount mount = GunnerUtility.ArmedMountOf(__instance.pawn);
            if (mount == null)
            {
                foreach (Gizmo gizmo in __result) yield return gizmo;
                yield break;
            }
            // Vanilla's test for a multi-selection with different weapons; the patches below
            // make it count the mount's weapon.
            if (PawnAttackGizmoUtility.CanShowEquipmentGizmos())
            {
                yield return GunnerUtility.FireCommand(mount);
            }
        }
    }

    /// <summary>
    /// The fire-at-will toggle. Vanilla shows it for a drafted pawn holding a ranged weapon.
    /// A gunner gets one whether drafted or not, and it switches the mount's hold fire,
    /// the same setting as the mount's own Hold fire toggle. Not for Perspective Shift's
    /// avatar, which never fires by itself.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_DraftController), "GetGizmos")]
    public static class Patch_Pawn_DraftController_GetGizmos
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn_DraftController __instance)
        {
            Building_WeaponMount mount = GunnerUtility.ArmedMountOf(__instance.pawn);
            if (mount == null)
            {
                foreach (Gizmo gizmo in __result) yield return gizmo;
                yield break;
            }
            bool show = !mount.AvatarControlled;
            bool shown = false;
            foreach (Gizmo gizmo in __result)
            {
                if (gizmo is Command_Toggle toggle && toggle.tutorTag == "FireAtWillToggle")
                {
                    if (show && !shown) yield return GunnerUtility.FireAtWillToggle(mount);
                    shown = true;
                    continue;
                }
                yield return gizmo;
            }
            if (show && !shown) yield return GunnerUtility.FireAtWillToggle(mount);
        }
    }

    /// <summary>
    /// Vanilla's multi-selection checks read pawn.equipment.Primary (and PrimaryEq). These
    /// make them read the mounted weapon for a gunner, so a gunner counts as armed with it:
    /// it decides whether the melee and squad attack buttons show and whether squad attack
    /// can target the ground. The Primary calls are swapped for GunnerUtility's, which
    /// leaves the stack as it was.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_PawnAttackGizmoUtility_GunnerWeapon
    {
        private static readonly string[] Names =
        {
            "AtLeastOneSelectedPlayerPawnHasRangedWeapon",
            "AtLeastOneSelectedPlayerPawnHasNoWeapon",
            "AllSelectedPlayerPawnsCanTargetLocations"
        };

        public static bool Prepare()
        {
            foreach (string name in Names)
            {
                if (AccessTools.Method(typeof(PawnAttackGizmoUtility), name) == null)
                {
                    Log.Warning("[Mounted Weapons] PawnAttackGizmoUtility." + name + " was not found; gunners selected together may show the wrong attack buttons.");
                    return false;
                }
            }
            return true;
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in Names) yield return AccessTools.Method(typeof(PawnAttackGizmoUtility), name);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo primary = AccessTools.PropertyGetter(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Primary));
            MethodInfo primaryEq = AccessTools.PropertyGetter(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.PrimaryEq));
            MethodInfo effective = AccessTools.Method(typeof(GunnerUtility), nameof(GunnerUtility.EffectivePrimary));
            MethodInfo effectiveEq = AccessTools.Method(typeof(GunnerUtility), nameof(GunnerUtility.EffectivePrimaryEq));
            int swapped = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.Calls(primary))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = effective;
                    swapped++;
                }
                else if (ins.Calls(primaryEq))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = effectiveEq;
                    swapped++;
                }
                yield return ins;
            }
            if (swapped == 0)
            {
                Log.Warning("[Mounted Weapons] PawnAttackGizmoUtility." + original?.Name + " has changed; gunners selected together may show the wrong attack buttons.");
            }
        }
    }

    /// <summary>
    /// Vanilla's "different weapons" test, which decides between showing everyone's weapon
    /// buttons and showing squad attack. A gunner counts as holding the mounted weapon, but
    /// as a different weapon from the same kind held in hand: the two buttons target with
    /// different verbs and can't be merged into one click. A postfix that recomputes the
    /// answer with vanilla's loop and that one change; vanilla's body only reads the
    /// selection, so running it first costs nothing else.
    /// </summary>
    [HarmonyPatch(typeof(PawnAttackGizmoUtility), "AtLeastTwoSelectedPlayerPawnsHaveDifferentWeapons")]
    public static class Patch_PawnAttackGizmoUtility_DifferentWeapons
    {
        public static void Postfix(ref bool __result)
        {
            __result = false;
            if (Find.Selector.NumSelected <= 1) return;
            ThingDef firstDef = null;
            bool firstMounted = false;
            bool any = false;
            List<object> selected = Find.Selector.SelectedObjectsListForReading;
            for (int i = 0; i < selected.Count; i++)
            {
                if (!(selected[i] is Pawn pawn) || !GunnerUtility.CanOrder(pawn)) continue;
                Building_WeaponMount mount = GunnerUtility.ArmedMountOf(pawn);
                ThingDef def = mount != null ? mount.MountedWeapon.def : pawn.equipment?.Primary?.def;
                bool mounted = mount != null;
                if (!any)
                {
                    firstDef = def;
                    firstMounted = mounted;
                    any = true;
                }
                else if (def != firstDef || mounted != firstMounted)
                {
                    __result = true;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Squad attack. Vanilla orders each selected drafted pawn through
    /// FloatMenuUtility.GetAttackAction, which the next patch sends to a drafted gunner's
    /// mount. Undrafted gunners are added here, since a gunner is armed either way, and
    /// the hover draws each gunner's firing field in place of their own weapon's range.
    /// </summary>
    [HarmonyPatch(typeof(PawnAttackGizmoUtility), "GetSquadAttackGizmo")]
    public static class Patch_PawnAttackGizmoUtility_SquadAttack
    {
        public static void Postfix(Gizmo __result)
        {
            if (!(__result is Command_Target command)) return;
            Action<LocalTargetInfo> action = command.action;
            command.action = target =>
            {
                action?.Invoke(target);
                foreach (object obj in Find.Selector.SelectedObjects)
                {
                    if (!(obj is Pawn pawn) || pawn.Drafted || !GunnerUtility.CanOrder(pawn)) continue;
                    GunnerUtility.ArmedMountOf(pawn)?.OrderAttack(target);
                }
            };
            command.onUpdate = target =>
            {
                foreach (object obj in Find.Selector.SelectedObjects)
                {
                    if (!(obj is Pawn pawn) || !GunnerUtility.CanOrder(pawn)) continue;
                    Building_WeaponMount mount = GunnerUtility.ArmedMountOf(pawn);
                    if (mount != null)
                    {
                        mount.DrawFireField();
                        continue;
                    }
                    if (!pawn.Drafted || pawn.equipment == null) continue;
                    // Vanilla's own hover for everyone else.
                    ThingWithComps weapon = null;
                    foreach (ThingWithComps eq in pawn.equipment.AllEquipmentListForReading)
                    {
                        if (eq.def.IsRangedWeapon)
                        {
                            weapon = eq;
                            break;
                        }
                    }
                    if (weapon == null || !weapon.TryGetComp(out CompEquippable comp)) continue;
                    foreach (Verb verb in comp.AllVerbs)
                    {
                        if (!verb.verbProps.hasStandardCommand) continue;
                        verb.verbProps.DrawRadiusRing(pawn.Position, verb);
                        break;
                    }
                }
            };
        }
    }

    /// <summary>
    /// The attack order behind squad attack. For a gunner it becomes an order to the mount,
    /// so the gunner stays on it. The mount turns down targets outside its arc or range
    /// with its own message. A postfix: vanilla's choice has no side effects and is
    /// simply overwritten.
    /// </summary>
    [HarmonyPatch(typeof(FloatMenuUtility), nameof(FloatMenuUtility.GetAttackAction))]
    public static class Patch_FloatMenuUtility_GetAttackAction
    {
        public static void Postfix(Pawn pawn, LocalTargetInfo target, ref string failStr, ref Action __result)
        {
            Building_WeaponMount mount = GunnerUtility.ArmedMountOf(pawn);
            if (mount == null) return;
            failStr = null;
            __result = () => mount.OrderAttack(target);
        }
    }

    /// <summary>
    /// The hit-chance tooltip with a gunner selected describes the mount's shot: the mount's
    /// verb and range, the gunner's skill and the mount's stability (through the
    /// HitFactorFromShooter patch), and the firing arc. Vanilla shows this tooltip only for
    /// a drafted pawn; a gunner gets it either way. Runs early, so Combat Extended's compat
    /// can replace the text for a CE weapon after CE's own postfix has seen it.
    /// </summary>
    [HarmonyPatch(typeof(TooltipUtility), nameof(TooltipUtility.ShotCalculationTipString))]
    [HarmonyPriority(Priority.High)]
    public static class Patch_TooltipUtility_ShotCalculationTipString_Gunner
    {
        public static void Postfix(Thing target, ref string __result)
        {
            if (!(Find.Selector.SingleSelectedThing is Pawn pawn) || pawn == target) return;
            Building_WeaponMount mount = GunnerUtility.ArmedMountOf(pawn);
            if (mount == null || mount == target) return;
            Verb verb = mount.AttackVerb;

            StringBuilder sb = new StringBuilder();
            sb.Append("ShotBy".Translate(pawn.LabelShort, pawn) + ": ");
            if (mount.InArc(target) && verb.CanHitTarget(target))
            {
                sb.Append(ShotReport.HitReportFor(mount, verb, target).GetTextReadout());
            }
            else
            {
                sb.AppendLine("CannotHit".Translate());
            }
            if (target is Pawn animal && animal.Faction == null && !animal.InAggroMentalState && animal.AnimalOrWildMan())
            {
                float chance = PawnUtility.GetManhunterOnDamageChance(animal, mount);
                if (chance > 0f)
                {
                    sb.AppendLine();
                    sb.AppendLine(string.Format("{0}: {1}", "ManhunterPerHit".Translate(), chance.ToStringPercent()));
                }
            }
            __result = sb.ToString();
        }
    }

    /// <summary>
    /// The weapon under the colonist bar portrait. ColonistBarOnGUI reads
    /// pawn.equipment?.Primary and pawn.Drafted inline, so the two calls are swapped for
    /// GunnerUtility's: a gunner shows the mounted weapon, and counts as drafted for the
    /// "while drafted" setting. Each swap leaves the stack as it was.
    /// </summary>
    [HarmonyPatch(typeof(ColonistBar), nameof(ColonistBar.ColonistBarOnGUI))]
    public static class Patch_ColonistBar_PortraitWeapon
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo primary = AccessTools.PropertyGetter(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Primary));
            MethodInfo drafted = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Drafted));
            MethodInfo effective = AccessTools.Method(typeof(GunnerUtility), nameof(GunnerUtility.EffectivePrimary));
            MethodInfo portraitDrafted = AccessTools.Method(typeof(GunnerUtility), nameof(GunnerUtility.PortraitDrafted));
            int swapped = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.Calls(primary))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = effective;
                    swapped++;
                }
                else if (ins.Calls(drafted))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = portraitDrafted;
                    swapped++;
                }
                yield return ins;
            }
            if (swapped == 0)
            {
                Log.Warning("[Mounted Weapons] ColonistBar.ColonistBarOnGUI has changed; gunners show their own weapon under the portrait.");
            }
        }
    }
}
