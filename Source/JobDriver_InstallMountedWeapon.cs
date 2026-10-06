using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace MountedWeapons
{
    /// <summary>Target A: the mount. Target B: the weapon.</summary>
    public class JobDriver_InstallMountedWeapon : JobDriver
    {
        private const int InstallTicks = 180;

        private Building_WeaponMount Mount => (Building_WeaponMount)job.GetTarget(TargetIndex.A).Thing;

        private Thing Weapon => job.GetTarget(TargetIndex.B).Thing;

        // Only the weapon is reserved. The gunner manning the mount holds a reservation on
        // it, and the weapon can be swapped while they man it; reserving the weapon is
        // enough to stop two haulers fetching it.
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            // The player cancelled or picked a different weapon.
            this.FailOn(() => Mount.pendingWeapon != Weapon);

            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.B)
                .FailOnSomeonePhysicallyInteracting(TargetIndex.B);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_General.Wait(InstallTicks)
                .FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch)
                .WithProgressBarToilDelay(TargetIndex.A);

            Toil install = ToilMaker.MakeToil("InstallMountedWeapon");
            install.initAction = () =>
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                if (carried == null || carried != Weapon)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                pawn.carryTracker.innerContainer.Remove(carried);
                Mount.InstallWeapon(carried);
            };
            install.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return install;
        }
    }
}
