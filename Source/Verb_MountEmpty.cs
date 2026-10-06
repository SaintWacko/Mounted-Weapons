using Verse;

namespace MountedWeapons
{
    /// <summary>
    /// The verb on the placeholder gun an empty mount holds. Building_TurretGun assumes
    /// it always has a gun, so instead of nulling it the empty mount carries this one,
    /// which is never available: TryStartShootSomething bails on !Available() before
    /// it looks for targets.
    /// </summary>
    public class Verb_MountEmpty : Verb
    {
        public override bool Available() => false;

        protected override bool TryCastShot() => false;
    }
}
