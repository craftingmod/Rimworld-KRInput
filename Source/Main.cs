using HarmonyLib;
using Verse;

namespace RimWorldKRInput
{
    [StaticConstructorOnStartup]
    internal static class HarmonyPatchEntryPointMock
    {
        static HarmonyPatchEntryPointMock()
        {
            var harmony = new Harmony("rimworld.krinput.entrypoint-mock");
            harmony.PatchAll(typeof(HarmonyPatchEntryPointMock).Assembly);
            Log.Message("[KRInput] Harmony patch entry-point mock initialized.");
        }
    }
}
