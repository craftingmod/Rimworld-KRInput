using HarmonyLib;
using Verse;

namespace RimWorldKRInput
{
    [StaticConstructorOnStartup]
    internal static class HarmonyPatchEntryPoint
    {
        static HarmonyPatchEntryPoint()
        {
            ImePatches.Install();
        }
    }
}
