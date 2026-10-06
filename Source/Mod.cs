using HarmonyLib;
using Verse;

namespace MountedWeapons
{
    public class MountedWeaponsMod : Mod
    {
        public const string HarmonyId = "saintwacko.MountedWeapons";

        public MountedWeaponsMod(ModContentPack content) : base(content)
        {
            // PatchAll only reflects over this assembly; no defs or Unity objects
            // are touched here, which matters because the Mod constructor runs on
            // a loading thread before DefOfs are bound.
            new Harmony(HarmonyId).PatchAll();
        }
    }
}
