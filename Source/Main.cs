using Verse;

namespace RimWorldKRInput
{
    [StaticConstructorOnStartup]
    public static class Start
    {
        static Start()
        {
            ImeProbe.Install();
        }
    }
}
