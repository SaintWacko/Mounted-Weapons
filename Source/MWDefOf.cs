using RimWorld;
using Verse;

namespace MountedWeapons
{
    [DefOf]
    public static class MWDefOf
    {
        public static JobDef MW_InstallMountedWeapon;

        static MWDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(MWDefOf));
        }
    }

    [StaticConstructorOnStartup]
    public static class MWTex
    {
        public static readonly UnityEngine.Texture2D ChooseWeapon = ContentFinder<UnityEngine.Texture2D>.Get("UI/Commands/MW_ChooseWeapon");
        public static readonly UnityEngine.Texture2D RemoveWeapon = ContentFinder<UnityEngine.Texture2D>.Get("UI/Commands/MW_RemoveWeapon");
        public static readonly UnityEngine.Texture2D Cancel = ContentFinder<UnityEngine.Texture2D>.Get("UI/Designators/Cancel");
    }
}
