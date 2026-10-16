using HarmonyLib;
using Verse;

namespace RimWorldKRInput
{
    [StaticConstructorOnStartup]
    internal static class HarmonyPatchEntryPoint
    {
        static HarmonyPatchEntryPoint()
        {
            // Deprecated: RimWorld 1.7.4980 rev60 provides its own Windows IME bridge.
            // ImePatches.Install();
        }
    }
}
