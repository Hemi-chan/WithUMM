using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace WithUMM.Diagnostics
{
    internal static class PatchConflictScanner
    {
        internal static void Report(Assembly provider, BootTrace trace)
        {
            if (provider == null) return;
            try
            {
                var h2 = provider.GetType("HarmonyLib.Harmony", true);
                var all = (IEnumerable)ReflectionAccess.Method(h2, "GetAllPatchedMethods").Invoke(null, null);
                var hxMethods = new HashSet<MethodBase>(HarmonyLib.Harmony.GetAllPatchedMethods());
                var count = 0;
                foreach (MethodBase method in all)
                {
                    if (!hxMethods.Contains(method)) continue;
                    count++;
                    var hxOwners = HarmonyLib.Harmony.GetPatchInfo(method).Owners;
                    var h2Info = ReflectionAccess.Method(h2, "GetPatchInfo", typeof(MethodBase)).Invoke(null, new object[] { method });
                    var h2Owners = ((IEnumerable)ReflectionAccess.Get(h2Info, "Owners")).Cast<string>();
                    trace.Event("Shared patch target=" + method.DeclaringType?.FullName + "." + method.Name +
                        "; UMM owners=" + string.Join(",", h2Owners) + "; ML owners=" + string.Join(",", hxOwners));
                }
                if (count > 0) trace.Warning("Shared targets=" + count + ". Wrapper composition preserves both engines, but before/after/priority and reverse snapshots remain engine-local. Verify affected gameplay.");
            }
            catch (Exception e) { trace.Failure("Patch ownership scan", e); }
        }
    }
}
