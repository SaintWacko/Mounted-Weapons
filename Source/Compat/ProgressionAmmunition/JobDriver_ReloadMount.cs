using System.Collections.Generic;
using ProgressionAmmunition;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace MountedWeapons.Ammo
{
    /// <summary>
    /// Fetch one ammo pack and load it into a mount. TargetA is the mount; TargetB is the
    /// source: an ammo station, a pack in storage, or a pack in the pawn's own inventory.
    /// Each branch ends with the pawn carrying one pack, so the walk to the mount and the
    /// loading are shared.
    ///
    /// A station hands over a real pack item so the pawn is seen carrying it. When
    /// stations are infinite (Progression: Ammunition's default) that pack cost nothing,
    /// so it is destroyed if the job is interrupted; dropping it would let a station mint
    /// free packs. A pack that came from storage or a station that charges is dropped, and
    /// one from the pawn's inventory goes back into it.
    /// </summary>
    public class JobDriver_ReloadMount : JobDriver
    {
        private const TargetIndex MountInd = TargetIndex.A;
        private const TargetIndex SourceInd = TargetIndex.B;
        private const int StationTicks = 300;
        private const int LoadTicks = 120;

        private enum PackOrigin { None, FreeStation, PaidStation, Storage, Inventory }

        private PackOrigin origin;

        private Building_WeaponMount Mount => job.GetTarget(MountInd).Thing as Building_WeaponMount;

        private CompAmmo Ammo
        {
            get
            {
                Building_WeaponMount mount = Mount;
                return mount != null && mount.HasWeapon ? mount.gun.TryGetComp<CompAmmo>() : null;
            }
        }

        private bool SourceIsInventory => !job.targetB.HasThing || job.targetB.Thing.ParentHolder is Pawn_InventoryTracker;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref origin, "origin", PackOrigin.None);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!pawn.Reserve(job.targetA, job, 1, -1, AmmoDefOf.MW_MountReload, errorOnFailed)) return false;
            Thing source = job.targetB.Thing;
            if (source is Building_AmmoRecharger) return pawn.Reserve(source, job, 1, -1, null, errorOnFailed);
            if (source != null && source.Spawned) return pawn.Reserve(source, job, 10, 1, null, errorOnFailed);
            return true;
        }

        private int Scaled(int baseTicks)
        {
            float speed = DefsOf.PA_AmmoRefillSpeed != null ? pawn.GetStatValue(DefsOf.PA_AmmoRefillSpeed) : 1f;
            return Mathf.RoundToInt(baseTicks / Mathf.Max(speed, 0.1f));
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(MountInd);
            this.FailOn(() =>
            {
                CompAmmo comp = Ammo;
                return comp == null || comp.CurAmmo >= comp.MaxAmmo;
            });
            AddFinishAction(ReturnUnusedPack);

            Toil gotoMount = Toils_Goto.GotoThing(MountInd, PathEndMode.Touch);
            Toil gotoStoredPack = Toils_Goto.GotoThing(SourceInd, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(SourceInd)
                .FailOnSomeonePhysicallyInteracting(SourceInd);
            Toil takeFromInventory = ToilMaker.MakeToil("MW_TakePackFromInventory");
            takeFromInventory.initAction = () =>
            {
                ThingDef packDef = Ammo?.ConsumableDef;
                Thing pack = packDef != null ? MountAmmo.CarriedPack(pawn, packDef) : null;
                if (pack == null || !pawn.inventory.innerContainer.TryTransferToContainer(pack, pawn.carryTracker.innerContainer, 1))
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                origin = PackOrigin.Inventory;
            };

            yield return Toils_Jump.JumpIf(takeFromInventory, () => SourceIsInventory);
            yield return Toils_Jump.JumpIf(gotoStoredPack, () => !(job.targetB.Thing is Building_AmmoRecharger));

            // Station: wait there while "grabbing ammo", then carry one pack away.
            yield return Toils_Goto.GotoThing(SourceInd, PathEndMode.Touch).FailOnDespawnedNullOrForbidden(SourceInd);
            yield return Toils_General.Wait(Scaled(StationTicks), SourceInd)
                .FailOnDespawnedNullOrForbidden(SourceInd)
                .FailOn(() =>
                {
                    CompAmmo comp = Ammo;
                    return comp == null || !(job.targetB.Thing is Building_AmmoRecharger station) || !station.CanRecharge(comp);
                })
                .WithProgressBarToilDelay(SourceInd);
            Toil takeFromStation = ToilMaker.MakeToil("MW_TakePackFromStation");
            takeFromStation.initAction = () =>
            {
                ThingDef packDef = Ammo?.ConsumableDef;
                if (packDef == null || !(job.targetB.Thing is Building_AmmoRecharger station))
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                Thing pack = ThingMaker.MakeThing(packDef);
                if (!pawn.carryTracker.TryStartCarry(pack))
                {
                    pack.Destroy();
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                bool free = ProgressionAmmunitionMod.settings == null || ProgressionAmmunitionMod.settings.refillBuildingsAreInfinite;
                origin = free ? PackOrigin.FreeStation : PackOrigin.PaidStation;
                station.ConsumeAmmoRefill();
            };
            yield return takeFromStation;
            yield return Toils_Jump.Jump(gotoMount);

            // Stored pack: pick one up.
            yield return gotoStoredPack;
            Toil pickUp = Toils_Haul.StartCarryThing(SourceInd);
            pickUp.AddFinishAction(() => origin = PackOrigin.Storage);
            yield return pickUp;
            yield return Toils_Jump.Jump(gotoMount);

            // Pack from the pawn's own inventory.
            yield return takeFromInventory;

            yield return gotoMount;
            yield return Toils_General.Wait(Scaled(LoadTicks), MountInd)
                .FailOnCannotTouch(MountInd, PathEndMode.Touch)
                .WithProgressBarToilDelay(MountInd);
            Toil load = ToilMaker.MakeToil("MW_LoadMount");
            load.initAction = () =>
            {
                CompAmmo comp = Ammo;
                Thing pack = pawn.carryTracker.CarriedThing;
                if (comp == null || pack == null || pack.def != comp.ConsumableDef)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                Thing one = pack.stackCount > 1 ? pack.SplitOff(1) : pack;
                one.Destroy();
                origin = PackOrigin.None;
                comp.RefillAmmo();
                comp.ReloadSound?.PlayOneShot(new TargetInfo(Mount.Position, Mount.Map));
            };
            yield return load;
        }

        private void ReturnUnusedPack(JobCondition condition)
        {
            Thing pack = pawn.carryTracker?.CarriedThing;
            if (origin == PackOrigin.None || pack == null || !RefillUtility.IsRefill(pack.def)) return;
            switch (origin)
            {
                case PackOrigin.FreeStation:
                    pack.Destroy();
                    break;
                case PackOrigin.Inventory:
                    if (!pawn.inventory.innerContainer.TryAddOrTransfer(pack))
                    {
                        pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                    }
                    break;
                default:
                    if (pawn.Spawned)
                    {
                        pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                    }
                    break;
            }
            origin = PackOrigin.None;
        }
    }
}
