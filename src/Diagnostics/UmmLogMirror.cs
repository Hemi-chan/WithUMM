using System;
using System.Reflection;
using HarmonyLib;

namespace WithUMM.Diagnostics
{
    internal static class UmmLogMirror
    {
        private static BootTrace trace;
        [ThreadStatic] private static bool writing;
        internal static void Install(HarmonyLib.Harmony patcher, Assembly umm, BootTrace output)
        {
            trace = output;
            var logger = umm.GetType("UnityModManagerNet.UnityModManager+Logger", true);
            patcher.Patch(ReflectionAccess.Method(logger, "Write", typeof(string), typeof(bool)),
                postfix: new HarmonyMethod(typeof(UmmLogMirror), nameof(AfterWrite)));
        }
        private static void AfterWrite(string __0)
        {
            if (writing) return;
            writing = true;
            try { trace.Event("UMM " + __0); }
            catch { /* Logging must never break the original UMM logger. */ }
            finally { writing = false; }
        }
    }
}
