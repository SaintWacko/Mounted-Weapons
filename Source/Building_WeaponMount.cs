using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace MountedWeapons
{
    /// <summary>
    /// A turret whose gun is a real weapon item the player installed.
    ///
    /// Building_TurretGun keeps its gun in the public <c>gun</c> field and saves it with
    /// Scribe_Deep, so the installed weapon is simply assigned there: quality, art,
    /// hit points and every comp ride along and come back out unchanged. An empty mount
    /// holds a placeholder gun (MW_EmptyMountGun) whose verb is never available,
    /// because the base class assumes the gun is never null.
    /// </summary>
    [StaticConstructorOnStartup]
    public class Building_WeaponMount : Building_TurretGun
    {
        /// <summary>The weapon the player picked, waiting for a colonist to bring it.</summary>
        public Thing pendingWeapon;

        /// <summary>
        /// Tick at which a warmup this mount started is due to finish. Vanilla turrets use
        /// one warmup per building def; a mount uses the weapon's own aim time scaled by
        /// its gunner, so BeginBurst runs the warmup itself (see BeginBurst).
        /// </summary>
        private int fireReadyTick = -1;

        // The range-scaled VerbProperties clones currently on the mounted weapon's
        // verbs. Not saved: verbs are rebuilt from the def on load and the clones
        // are reapplied.
        private readonly List<VerbProperties> appliedProps = new List<VerbProperties>();
        // The weapon def's own range for each clone, so a settings change can rescale them.
        private readonly List<float> baseRanges = new List<float>();

        private MountExtension extInt;

        public MountExtension Ext
        {
            get
            {
                if (extInt == null)
                {
                    extInt = def.GetModExtension<MountExtension>() ?? new MountExtension();
                }
                return extInt;
            }
        }

        public bool HasWeapon => gun != null && gun.def != def.building.turretGunDef;

        public Thing MountedWeapon => HasWeapon ? gun : null;

        public Pawn Gunner => mannableComp?.ManningPawn;

        /// <summary>True for the manned line (has CompMannable), false for automated mounts.</summary>
        public bool IsManned => mannableComp != null;

        /// <summary>
        /// Ticks left on the current aim. Setting it also moves the tick the shot is due on,
        /// so the weapon fires when the new count runs out instead of starting a second warmup
        /// (see BeginBurst). Meant for calls made while the mount is firing, which is where
        /// Combat Extended adds its aimed-shot time and shortens repeat shots.
        /// </summary>
        public int WarmupTicksLeft
        {
            get => burstWarmupTicksLeft;
            set
            {
                burstWarmupTicksLeft = value;
                fireReadyTick = value > 0 ? Find.TickManager.TicksGame + value : -1;
            }
        }

        // The base draws the turret top from the turret gun def's texture. The mount
        // draws the real weapon instead (DrawAt), so the top is drawn clear.
        public override Material TurretTopMaterial => BaseContent.ClearMat;

        // While Perspective Shift's inhabited pawn mans the mount, the player aims with the
        // mouse, so there is no forced target; the base Tick clears any it had.
        protected override bool CanSetForcedTarget =>
            HasWeapon && !AvatarControlled && (base.CanSetForcedTarget || (Ext.allowForcedTarget && Faction == Faction.OfPlayer));

        protected override bool HideForceTargetGizmo => !HasWeapon || AvatarControlled;

        private IntVec3 DropCell => def.hasInteractionCell ? InteractionCell : Position;

        // ---------------------------------------------------------------- firing arc

        public float Arc => MountSettings.Get.ArcFor(def);

        public bool InArc(LocalTargetInfo targ)
        {
            if (!targ.IsValid) return true;
            if (FiringArc.Unlimited(Arc)) return true;
            Vector3 point = targ.HasThing ? targ.Thing.DrawPos : targ.Cell.ToVector3Shifted();
            // A target on another As above, So below II level is tested where it sits in this level.
            point = ModCompat.ToBandOf(Map, Position, targ.Cell, point);
            return FiringArc.Contains(DrawPos, Rotation, Arc, point);
        }

        public override LocalTargetInfo TryFindNewTarget()
        {
            // The inhabited pawn fires only when the player clicks (TryAvatarFire).
            if (AvatarControlled) return LocalTargetInfo.Invalid;
            if (MountAmmoHandler.Active != null && MountAmmoHandler.Active.OutOfAmmo(this)) return LocalTargetInfo.Invalid;
            if (FiringArc.Unlimited(Arc) || !HasWeapon) return base.TryFindNewTarget();
            // Same search as the base, with the arc added to its validator. The base's
            // validator is private, so its rules are repeated in IsValidMountTarget.
            IAttackTargetSearcher searcher = Gunner != null ? (IAttackTargetSearcher)Gunner : this;
            TargetScanFlags flags = TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable;
            if (!AttackVerb.ProjectileFliesOverhead())
            {
                flags |= TargetScanFlags.NeedLOSToAll | TargetScanFlags.LOSBlockableByGas;
            }
            if (AttackVerb.IsIncendiary_Ranged())
            {
                flags |= TargetScanFlags.NeedNonBurning;
            }
            return (Thing)AttackTargetFinder.BestShootTargetFromCurrentPosition(searcher, flags,
                t => InArc(t) && IsValidMountTarget(t));
        }

        private bool IsValidMountTarget(Thing t)
        {
            if (!(t is Pawn pawn)) return true;
            if (Faction == Faction.OfPlayer && pawn.IsPrisoner) return false;
            if (AttackVerb.ProjectileFliesOverhead())
            {
                RoofDef roof = Map.roofGrid.RoofAt(t.Position);
                if (roof != null && roof.isThickRoof) return false;
            }
            if (mannableComp == null) return !GenAI.MachinesLike(Faction, pawn);
            return !(pawn.RaceProps.Animal && pawn.Faction == Faction.OfPlayer);
        }

        public override void DrawExtraSelectionOverlays()
        {
            float radius = HasWeapon ? AttackVerb.EffectiveRange : 12f;
            if (FiringArc.Unlimited(Arc))
            {
                // A full circle is exactly what Building_TurretGun draws.
                base.DrawExtraSelectionOverlays();
                return;
            }

            // Building_TurretGun's override always draws a full range ring, which sat on top
            // of the arc and read as if the mount could fire all round. C# can't call past it
            // to Building's version, so this is its chain (Thing, ThingWithComps, Building,
            // Building_TurretGun, RimWorld 1.6) with the outer ring left out: the arc's curved
            // edge marks the range instead.
            // Cost: patches on those four methods don't run for limited-arc mounts. The one
            // known case is Vehicle Map Framework, which transpiles Building_TurretGun's
            // version to map the forced-target cell off a vehicle map; on a vehicle, the
            // target line below would point at the unmapped cell. Visual only.
            if (def.specialDisplayRadius > 0.1f) GenDraw.DrawRadiusRing(Position, def.specialDisplayRadius);
            if (def.drawPlaceWorkersWhileSelected && def.PlaceWorkers != null)
            {
                for (int i = 0; i < def.PlaceWorkers.Count; i++)
                    def.PlaceWorkers[i].DrawGhost(def, Position, Rotation, Color.white, this);
            }
            GenDraw.DrawInteractionCells(def, Position, Rotation);
            foreach (ThingComp comp in AllComps) comp.PostDrawExtraSelectionOverlays();
            Blueprint_Install blueprint = InstallBlueprintUtility.ExistingBlueprintFor(this);
            if (blueprint != null) GenDraw.DrawLineBetween(this.TrueCenter(), blueprint.TrueCenter());

            FiringArc.DrawField(Position, Rotation, Arc, radius, Map);
            if (HasWeapon)
            {
                // The minimum range stays a full ring: it's where the gun can't fire at all.
                float minRange = AttackVerb.verbProps.EffectiveMinRange(allowAdjacentShot: true);
                if (minRange > 0.1f && minRange < 90f && minRange < radius) GenDraw.DrawRadiusRing(Position, minRange);
            }
            if (burstWarmupTicksLeft > 0)
            {
                GenDraw.DrawAimPie(this, CurrentTarget, (int)(burstWarmupTicksLeft * 0.5f), def.size.x * 0.5f);
            }
            if (forcedTarget.IsValid && (!forcedTarget.HasThing || forcedTarget.Thing.Spawned))
            {
                Vector3 b = forcedTarget.HasThing ? forcedTarget.Thing.TrueCenter() : forcedTarget.Cell.ToVector3Shifted();
                Vector3 a = this.TrueCenter();
                b.y = AltitudeLayer.MetaOverlays.AltitudeFor();
                a.y = b.y;
                GenDraw.DrawLineBetween(a, b, ForcedTargetLineMat);
            }
        }

        private void RescaleRanges()
        {
            for (int i = 0; i < appliedProps.Count; i++)
            {
                appliedProps[i].range = baseRanges[i] * Ext.rangeMultiplier;
            }
        }

        public void Notify_SettingsChanged()
        {
            RescaleRanges();
            if (forcedTarget.IsValid && !InArc(forcedTarget))
            {
                ResetTargeting();
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            PrepareMountedWeapon();
            // A mount reinstalled from a minified item keeps its clones; settings may have
            // changed while it was packed up.
            RescaleRanges();
            MountCombatHandler.Active?.Notify_Spawned(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            MountCombatHandler.Active?.Notify_DeSpawned(this);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref pendingWeapon, "pendingWeapon");
            Scribe_Values.Look(ref fireReadyTick, "fireReadyTick", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // The base already rebound caster and callback; this adds the range clones.
                PrepareMountedWeapon();
            }
        }

        protected override void Tick()
        {
            bool avatar = AvatarControlled;
            if (avatar != avatarControlledLast)
            {
                avatarControlledLast = avatar;
                avatarAim = -1f;
                // An automatic aim in progress when the player takes over must not fire, and
                // neither must the player's aim once they let go.
                ResetTargeting();
            }
            if (forcedTarget.IsValid && !InArc(forcedTarget))
            {
                // A forced target walked out of the arc.
                ResetTargeting();
            }
            base.Tick();
            if (avatar && avatarAim >= 0f && !AvatarShooting)
            {
                // TurretTopTick turns toward the last target or sweeps idly; the player's aim wins.
                Top.CurRotation = avatarAim;
            }
            if (Spawned && !FiringArc.Unlimited(Arc))
            {
                // Keeps idle sweeps, and the turn toward any target, inside the arc.
                Top.CurRotation = FiringArc.Clamp(Rotation, Arc, Top.CurRotation);
            }
            if (pendingWeapon != null && this.IsHashIntervalTick(250) && !PendingWeaponStillValid())
            {
                pendingWeapon = null;
            }
            if (Spawned && this.IsHashIntervalTick(60))
            {
                CheckGunnerCanFire();
            }
            if (Spawned)
            {
                MountAmmoHandler.Active?.MountTick(this);
            }
        }

        /// <summary>
        /// The Man option is greyed out for a pawn who can't fire the weapon, but a weapon
        /// can be swapped while someone is manning, and other mods start the job their own
        /// way. This catches both.
        /// </summary>
        private void CheckGunnerCanFire()
        {
            Pawn gunner = Gunner;
            if (gunner == null || !HasWeapon || gunner.CurJobDef != JobDefOf.ManTurret) return;
            string reason = ModCompat.CannotFireReason(gunner, gun);
            if (reason == null) return;
            gunner.jobs.EndCurrentJob(JobCondition.InterruptForced);
            if (Faction == Faction.OfPlayer)
            {
                Messages.Message("MW_GunnerCannotFire".Translate(gunner.Named("PAWN"), gun.Named("WEAPON"), reason.Named("REASON")),
                    this, MessageTypeDefOf.RejectInput, historical: false);
            }
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            string reason = HasWeapon && IsManned ? ModCompat.CannotFireReason(selPawn, gun) : null;
            // CompMannable's option, matched by its label: the comp offers no hook of its own.
            string manLabel = reason != null ? "OrderManThing".Translate(LabelShort, this).ToString() : null;
            foreach (FloatMenuOption option in base.GetFloatMenuOptions(selPawn))
            {
                if (reason != null && option.Label == manLabel)
                {
                    yield return new FloatMenuOption("CannotManThing".Translate(LabelShort, this) + " (" + reason + ")", null);
                    continue;
                }
                yield return option;
            }
        }

        public bool PendingWeaponStillValid()
        {
            if (pendingWeapon == null || pendingWeapon.Destroyed) return false;
            // Spawned on this map, or being carried here by the installer.
            if (pendingWeapon.Spawned) return pendingWeapon.Map == Map;
            return pendingWeapon.ParentHolder is Pawn_CarryTracker carrier && carrier.pawn.Map == Map;
        }

        // ---------------------------------------------------------------- weapon swap

        /// <summary>
        /// Puts a weapon on the mount. The caller must already have taken it out of
        /// whatever held it (a pawn's carry tracker, usually). A weapon already on the
        /// mount is dropped beside it.
        /// </summary>
        public void InstallWeapon(Thing weapon)
        {
            if (HasWeapon && Spawned)
            {
                Thing old = RemoveWeapon();
                GenPlace.TryPlaceThing(old, DropCell, Map, ThingPlaceMode.Near);
            }
            gun = weapon;
            appliedProps.Clear();
            baseRanges.Clear();
            PrepareMountedWeapon();
            MountCombatHandler.Active?.Notify_WeaponInstalled(this, weapon);
            ResetTargeting();
            burstCooldownTicksLeft = 0;
            if (pendingWeapon == weapon)
            {
                pendingWeapon = null;
            }
        }

        /// <summary>
        /// Takes the weapon off the mount and returns it unspawned, with fresh verbs so
        /// a pawn can equip it normally. Returns null when the mount is empty.
        /// </summary>
        public Thing RemoveWeapon()
        {
            if (!HasWeapon) return null;
            Thing weapon = gun;
            MountCombatHandler.Active?.Notify_WeaponRemoved(this, weapon);
            CompEquippable eq = weapon.TryGetComp<CompEquippable>();
            if (eq != null)
            {
                // A new tracker drops the caster, the burst callback and the range
                // clones in one go; verbs are rebuilt lazily from the def.
                eq.verbTracker = new VerbTracker(eq);
            }
            MakeGun();
            appliedProps.Clear();
            baseRanges.Clear();
            ResetTargeting();
            return weapon;
        }

        private void PrepareMountedWeapon()
        {
            if (!HasWeapon) return;
            CompEquippable eq = gun.TryGetComp<CompEquippable>();
            if (eq == null) return;
            List<Verb> verbs = eq.AllVerbs;
            for (int i = 0; i < verbs.Count; i++)
            {
                Verb verb = verbs[i];
                verb.caster = this;
                verb.castCompleteCallback = BurstComplete;
                if (!appliedProps.Contains(verb.verbProps))
                {
                    // Clone so the range bonus applies to this weapon only, never to the def.
                    VerbProperties clone = verb.verbProps.MemberwiseClone();
                    clone.range = verb.verbProps.range * Ext.rangeMultiplier;
                    baseRanges.Add(verb.verbProps.range);
                    verb.verbProps = clone;
                    appliedProps.Add(clone);
                }
            }
        }

        private void ResetTargeting()
        {
            forcedTarget = LocalTargetInfo.Invalid;
            currentTargetInt = LocalTargetInfo.Invalid;
            burstWarmupTicksLeft = 0;
            fireReadyTick = -1;
        }

        // ---------------------------------------------------------------- firing

        protected override void BeginBurst()
        {
            // Both entry points land here: TryStartShootSomething (target just acquired;
            // the defs set turretBurstWarmupTime to 0) and the base Tick when
            // burstWarmupTicksLeft reaches zero. The first call starts the weapon's own
            // warmup; the call that arrives when that warmup ends fires. The two-tick
            // window stops a stale fireReadyTick (warmup cancelled when the gunner
            // left) from firing instantly later.
            if (MountAmmoHandler.Active != null && MountAmmoHandler.Active.OutOfAmmo(this))
            {
                // Keeps any forced target, so the mount picks it up again once reloaded.
                burstWarmupTicksLeft = 0;
                fireReadyTick = -1;
                return;
            }
            int now = Find.TickManager.TicksGame;
            // One tick early is allowed: WarmupTicksLeft set before the base Tick's countdown
            // (rather than from inside BeginBurst) runs out one tick sooner.
            if (fireReadyTick >= 0 && now >= fireReadyTick - 1 && now <= fireReadyTick + 2)
            {
                Fire();
                return;
            }
            int warmup = WarmupTicks();
            if (warmup <= 0)
            {
                Fire();
                return;
            }
            burstWarmupTicksLeft = warmup;
            fireReadyTick = now + warmup;
        }

        private void Fire()
        {
            fireReadyTick = -1;
            if (!InArc(CurrentTarget))
            {
                // The target left the arc during the warmup.
                ResetTargeting();
                return;
            }
            // Same body as Building_TurretGun.BeginBurst, with XP only for a burst that
            // actually started (vanilla trains in WarmupComplete, after CanHitTarget).
            if (AttackVerb.TryStartCastOn(CurrentTarget))
            {
                GiveGunnerShootingXp();
            }
            OnAttackedTarget(CurrentTarget);
        }

        public override void OrderAttack(LocalTargetInfo targ)
        {
            // A new order restarts aiming; a warmup that was about to finish must not
            // carry over to the new target. Compared after the base runs, because the
            // base turns down out-of-range orders without changing the target.
            if (targ.IsValid && !InArc(targ))
            {
                Messages.Message("MW_TargetOutsideArc".Translate(), this, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            LocalTargetInfo before = forcedTarget;
            base.OrderAttack(targ);
            if (forcedTarget != before)
            {
                fireReadyTick = -1;
            }
        }

        private int WarmupTicks()
        {
            // Perspective Shift drops the aiming delay for the weapon its avatar holds;
            // the same setting covers the mount the avatar is firing.
            if (ModCompat.AvatarSkipsAiming && AvatarControlled) return 0;
            float seconds = AttackVerb.verbProps.warmupTime * Ext.warmupFactor;
            Pawn gunner = Gunner;
            if (gunner != null)
            {
                // Carries Careful shooter / Trigger-happy and anything else on the stat.
                seconds *= gunner.GetStatValue(StatDefOf.AimingDelayFactor);
            }
            return seconds.SecondsToTicks();
        }

        protected override float BurstCooldownTime()
        {
            if (!HasWeapon) return 1f;
            // The stat includes weapon quality, as it does when a pawn holds the weapon.
            return gun.GetStatValue(StatDefOf.RangedWeapon_Cooldown) * Ext.cooldownFactor;
        }

        /// <summary>
        /// Verb_Shoot only trains pawn casters; the mount is the caster, so the gunner is
        /// trained here with vanilla's numbers (170 per cycle-second against enemies,
        /// 20 otherwise, no XP for shooting downed pawns).
        /// </summary>
        private void GiveGunnerShootingXp()
        {
            Pawn gunner = Gunner;
            if (gunner?.skills == null) return;
            if (MountCombatHandler.Active != null && MountCombatHandler.Active.VerbTrainsGunner(AttackVerb)) return;
            if (!(CurrentTarget.Thing is Pawn target) || target.Downed || target.IsColonyMech) return;
            float xp = target.HostileTo(gunner) ? 170f : 20f;
            xp *= AttackVerb.verbProps.AdjustedFullCycleTime(AttackVerb, gunner);
            gunner.skills.Learn(SkillDefOf.Shooting, xp);
        }

        // ---------------------------------------------------------------- Perspective Shift control

        public enum AvatarAimState { Ready, NoShot, Aiming, Cooldown }

        // The player's aim from the mouse, already held inside the arc; -1 for none.
        // Not saved: it is set again on the next frame the player is in control.
        private float avatarAim = -1f;
        private bool avatarControlledLast;

        /// <summary>Manned right now by the pawn the player inhabits in Perspective Shift.</summary>
        public bool AvatarControlled
        {
            get
            {
                Pawn gunner = Gunner;
                return gunner != null && HasWeapon && ModCompat.IsAvatar(gunner);
            }
        }

        private bool AvatarShooting => burstWarmupTicksLeft > 0 || AttackVerb.state == VerbState.Bursting;

        /// <summary>Turns the weapon toward a map point, stopping at the edge of the arc.</summary>
        public void AimAvatarAt(Vector3 point)
        {
            Vector3 dir = point - DrawPos;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return;
            float angle = dir.AngleFlat();
            if (!FiringArc.Unlimited(Arc)) angle = FiringArc.Clamp(Rotation, Arc, angle);
            avatarAim = angle;
            if (!AvatarShooting) Top.CurRotation = angle;
        }

        /// <summary>What Perspective Shift's reticle should show for this target.</summary>
        public AvatarAimState AvatarStateFor(LocalTargetInfo target)
        {
            if (burstWarmupTicksLeft > 0) return AvatarAimState.Aiming;
            if (AttackVerb.state == VerbState.Bursting || burstCooldownTicksLeft > 0) return AvatarAimState.Cooldown;
            return AvatarCanHit(target) ? AvatarAimState.Ready : AvatarAimState.NoShot;
        }

        private bool AvatarCanHit(LocalTargetInfo target)
        {
            if (!HasWeapon || !target.IsValid || !InArc(target)) return false;
            // Base Tick's own conditions for firing: power and the other comps Active reads, and stun.
            if (!Active || IsStunned) return false;
            if (MountAmmoHandler.Active != null && MountAmmoHandler.Active.OutOfAmmo(this)) return false;
            if (!AttackVerb.Available()) return false;
            if (AttackVerb.ProjectileFliesOverhead() && Map.roofGrid.Roofed(Position)) return false;
            return AttackVerb.CanHitTarget(target);
        }

        /// <summary>
        /// One burst at the player's target, through the normal BeginBurst so ammo, aiming
        /// time, XP and the cooldown work as they do for any gunner. Returns false, doing
        /// nothing, while aiming, firing or cooling down, so Perspective Shift's
        /// hold-to-fire can call this every frame.
        /// </summary>
        public bool TryAvatarFire(LocalTargetInfo target)
        {
            if (!AvatarControlled || AvatarStateFor(target) != AvatarAimState.Ready) return false;
            forcedTarget = LocalTargetInfo.Invalid;
            currentTargetInt = target;
            fireReadyTick = -1;
            BeginBurst();
            return true;
        }

        // ---------------------------------------------------------------- destruction

        /// <summary>
        /// Called just before the mount is packed into a minified item: drops the weapon
        /// beside it and forgets any weapon waiting to be installed. A minified mount sits
        /// in storage or a caravan, where nobody could take the weapon off it, and its
        /// Destroy has no map to drop the weapon on.
        /// </summary>
        public void Notify_Minifying()
        {
            pendingWeapon = null;
            if (!Spawned) return;
            Thing weapon = RemoveWeapon();
            // With nowhere to drop it (a boxed-in mount), the weapon goes back on and
            // packs up with the mount rather than being left unspawned and lost.
            if (weapon != null && !GenPlace.TryPlaceThing(weapon, DropCell, Map, ThingPlaceMode.Near))
            {
                InstallWeapon(weapon);
            }
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            IntVec3 pos = Position;
            bool wasSpawned = Spawned;
            string label = Label;

            Thing weapon = null;
            if (wasSpawned && mode != DestroyMode.Vanish)
            {
                weapon = RemoveWeapon();
            }

            base.Destroy(mode);

            if (!wasSpawned || map == null) return;

            bool killed = mode == DestroyMode.KillFinalize || mode == DestroyMode.KillFinalizeLeavingsOnly;
            if (weapon != null)
            {
                if (killed && weapon.def.useHitPoints)
                {
                    int damage = Mathf.RoundToInt(weapon.MaxHitPoints * Rand.Range(0f, MountSettings.Get.maxWeaponDamage));
                    weapon.HitPoints = Mathf.Max(1, weapon.HitPoints - damage);
                }
                GenPlace.TryPlaceThing(weapon, pos, map, ThingPlaceMode.Near);
            }

            if (Ext.coreDef != null && mode != DestroyMode.Vanish)
            {
                // The core is on the building's leavingsBlacklist, so vanilla never
                // refunds it. Deconstructing always returns it; destruction in combat
                // loses it on a roll.
                bool dropCore = true;
                if (killed)
                {
                    dropCore = !Rand.Chance(MountSettings.Get.coreLossChance);
                    string key = dropCore ? "MW_CoreSurvived" : "MW_CoreLost";
                    Messages.Message(key.Translate(Ext.coreDef.label, label), new TargetInfo(pos, map),
                        dropCore ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.NegativeEvent);
                }
                if (dropCore)
                {
                    GenPlace.TryPlaceThing(ThingMaker.MakeThing(Ext.coreDef), pos, map, ThingPlaceMode.Near);
                }
            }
        }

        // ---------------------------------------------------------------- drawing

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);
            if (!HasWeapon) return;
            Vector3 offset = new Vector3(Ext.weaponDrawOffset.x, 0f, Ext.weaponDrawOffset.y).RotatedBy(Rotation.AsAngle);
            Vector3 pivot = drawLoc + offset + Altitudes.AltIncVect * 2f;
            // Spray and beam verbs sweep across a path during a burst and report the cell
            // they are on through AimAngleOverride; vanilla TurretTop.DrawTurret draws with
            // it, so a mounted gatling or beam gun turns with its shots instead of pointing
            // at the original target. Not clamped to the firing arc: the gun points where
            // the rounds actually go.
            float aim = CurrentEffectiveVerb?.AimAngleOverride ?? Top.CurRotation;
            Vector3 mountPoint = pivot;
            if (MountSettings.Get.centerWeapons)
            {
                pivot -= ArtCentroidWorld(aim);
            }
            PawnRenderUtility.DrawEquipmentAiming(gun, pivot, aim);
            DrawShield(mountPoint, aim);
        }

        /// <summary>
        /// Draws the optional gun shield from MountExtension, turned to the weapon's aim and
        /// pushed forward along it. The back layer sits one altitude step below the gun and
        /// the optional top layer one step above it, so the barrel shows between them as if
        /// through the slot. Draw order doesn't matter; altitude (y) decides what's on top.
        /// Both layers mirror on the same aim range DrawEquipmentAiming flips the weapon on.
        /// </summary>
        private void DrawShield(Vector3 mountPoint, float aim)
        {
            Graphic back = Ext.ShieldGraphic;
            Graphic top = Ext.ShieldTopGraphic;
            if (back == null && top == null) return;
            Vector3 pos = mountPoint + new Vector3(0f, 0f, Ext.shieldOffset).RotatedBy(aim);
            Mesh mesh = aim > 200f && aim < 340f ? MeshPool.plane10Flip : MeshPool.plane10;
            Quaternion rot = Quaternion.AngleAxis(aim % 360f, Vector3.up);
            Vector3 scale = new Vector3(Ext.shieldDrawSize.x, 1f, Ext.shieldDrawSize.y);
            if (back != null)
            {
                // mountPoint is the gun's altitude, so one step down is under the gun.
                Graphics.DrawMesh(mesh, Matrix4x4.TRS(pos - Altitudes.AltIncVect, rot, scale), back.MatSingle, 0);
            }
            if (top != null)
            {
                Graphics.DrawMesh(mesh, Matrix4x4.TRS(pos + Altitudes.AltIncVect, rot, scale), top.MatSingle, 0);
            }
        }

        /// <summary>
        /// Where the weapon art's centroid lands, relative to the draw point, for the mesh
        /// and rotation PawnRenderUtility.DrawEquipmentAiming will use at this aim angle.
        /// Subtracting it puts the art's visual centre on the pivot.
        /// </summary>
        private Vector3 ArtCentroidWorld(float aimAngle)
        {
            Graphic graphic = gun.Graphic;
            Material mat = graphic is Graphic_StackCount stack
                ? stack.SubGraphicForStackCount(1, gun.def).MatSingleFor(gun)
                : graphic.MatSingleFor(gun);
            Vector2 uv = WeaponArtCentroid.OffsetFor(mat?.mainTexture);
            if (uv == Vector2.zero) return Vector3.zero;

            // Mirrors DrawEquipmentAiming's choice of mesh and angle.
            float angle = aimAngle - 90f;
            bool flipped = aimAngle > 200f && aimAngle < 340f;
            if (flipped)
            {
                angle -= 180f;
                angle -= gun.def.equippedAngleOffset;
            }
            else
            {
                angle += gun.def.equippedAngleOffset;
            }
            Vector3 local = new Vector3(uv.x * graphic.drawSize.x * (flipped ? -1f : 1f), 0f, uv.y * graphic.drawSize.y);
            return Quaternion.AngleAxis(angle % 360f, Vector3.up) * local;
        }

        // ---------------------------------------------------------------- UI

        public override string GetInspectString()
        {
            StringBuilder sb = new StringBuilder();
            string baseString = base.GetInspectString();
            if (!baseString.NullOrEmpty())
            {
                sb.AppendLine(baseString);
            }
            if (HasWeapon)
            {
                sb.AppendLine("MW_InspectMounted".Translate(gun.LabelCap));
                sb.AppendLine("MW_InspectRange".Translate(AttackVerb.verbProps.range.ToString("F1"), Ext.rangeMultiplier.ToStringPercent()));
                string ammoLine = MountAmmoHandler.Active?.InspectLine(this);
                if (!ammoLine.NullOrEmpty())
                {
                    sb.AppendLine(ammoLine);
                }
            }
            else
            {
                sb.AppendLine("MW_InspectEmpty".Translate());
            }
            if (pendingWeapon != null)
            {
                sb.AppendLine("MW_InspectPending".Translate(pendingWeapon.LabelCap));
            }
            if (HasWeapon && mannableComp != null && Gunner == null)
            {
                sb.AppendLine("MW_InspectUnmanned".Translate());
            }
            return sb.ToString().TrimEndNewlines();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }
            if (Faction != Faction.OfPlayer) yield break;

            if (MountAmmoHandler.Active != null)
            {
                foreach (Gizmo gizmo in MountAmmoHandler.Active.Gizmos(this))
                {
                    yield return gizmo;
                }
            }

            yield return new Command_Action
            {
                defaultLabel = "MW_CommandChooseWeapon".Translate(),
                defaultDesc = "MW_CommandChooseWeaponDesc".Translate(),
                icon = MWTex.ChooseWeapon,
                action = () => Find.WindowStack.Add(new FloatMenu(WeaponChoices()))
            };

            if (pendingWeapon != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "MW_CommandCancelWeapon".Translate(),
                    defaultDesc = "MW_CommandCancelWeaponDesc".Translate(),
                    icon = MWTex.Cancel,
                    action = () =>
                    {
                        foreach (Building_WeaponMount mount in SelectedMounts())
                        {
                            mount.pendingWeapon = null;
                        }
                    }
                };
            }

            if (HasWeapon)
            {
                yield return new Command_Action
                {
                    defaultLabel = "MW_CommandRemoveWeapon".Translate(),
                    defaultDesc = "MW_CommandRemoveWeaponDesc".Translate(),
                    icon = MWTex.RemoveWeapon,
                    action = () =>
                    {
                        // Gizmos with the same label merge across a multi-selection,
                        // so this acts on every selected mount.
                        foreach (Building_WeaponMount mount in SelectedMounts())
                        {
                            Thing weapon = mount.RemoveWeapon();
                            if (weapon != null)
                            {
                                GenPlace.TryPlaceThing(weapon, mount.DropCell, mount.Map, ThingPlaceMode.Near);
                            }
                        }
                    }
                };
            }
        }

        private IEnumerable<Building_WeaponMount> SelectedMounts()
        {
            List<Building_WeaponMount> result = Find.Selector.SelectedObjects.OfType<Building_WeaponMount>().ToList();
            if (!result.Contains(this))
            {
                result.Add(this);
            }
            return result;
        }

        private List<FloatMenuOption> WeaponChoices()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            IEnumerable<Thing> candidates = Map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon)
                .Where(t => t.Spawned && !t.IsForbidden(Faction.OfPlayer) && MountUtility.CanMount(t, out _))
                .OrderBy(t => t.def.label)
                .ThenByDescending(t => t.TryGetQuality(out QualityCategory q) ? (int)q : -1);
            // Weapons another mount is already waiting for are listed last and greyed out.
            Dictionary<Thing, Building_WeaponMount> claimed = new Dictionary<Thing, Building_WeaponMount>();
            foreach (Building_WeaponMount other in Map.listerBuildings.AllBuildingsColonistOfClass<Building_WeaponMount>())
            {
                if (other != this && other.pendingWeapon != null)
                {
                    claimed[other.pendingWeapon] = other;
                }
            }
            foreach (Thing weapon in candidates.OrderBy(t => claimed.ContainsKey(t)))
            {
                Thing local = weapon;
                if (claimed.TryGetValue(local, out Building_WeaponMount owner))
                {
                    options.Add(new FloatMenuOption("MW_WeaponClaimed".Translate(local.LabelCap, owner.Label), null, local, Color.white,
                        mouseoverGuiAction: _ => { TargetHighlighter.Highlight(local); TargetHighlighter.Highlight(owner); }));
                    continue;
                }
                options.Add(new FloatMenuOption(local.LabelCap, () => pendingWeapon = local, local, Color.white,
                    mouseoverGuiAction: _ => TargetHighlighter.Highlight(local)));
            }
            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption("MW_NoMountableWeapons".Translate(), null));
            }
            return options;
        }
    }
}
