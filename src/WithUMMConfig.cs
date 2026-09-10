using MelonLoader;

namespace WithUMM
{
    internal sealed class WithUMMConfig
    {
        internal bool DiagnosticsOnly;
        internal bool MirrorLog;
        internal string UmmDirectory;
        internal static WithUMMConfig Load()
        {
            var category = MelonPreferences.CreateCategory("WithUMM");
            return new WithUMMConfig
            {
                DiagnosticsOnly = category.CreateEntry("DiagnosticsOnly", false,
                    "Observe startup only; do not change binding or boot UMM").Value,
                MirrorLog = category.CreateEntry("MirrorUmmLog", true,
                    "Mirror UMM messages into the MelonLoader log").Value,
                UmmDirectory = category.CreateEntry("UmmDirectory", "",
                    "Optional absolute path to the installed Managed/UnityModManager folder").Value
            };
        }
    }
}
