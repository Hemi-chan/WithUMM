using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace WithUMM.Boot
{
    internal static class ModsPathBinder
    {
        private static string modsPath;
        private static Type manager;
        internal static bool InitializedSuccessfully { get; private set; }

        internal static void Install(HarmonyLib.Harmony patcher, Assembly umm, string path)
        {
            modsPath = Path.GetFullPath(path);
            Directory.CreateDirectory(modsPath);
            manager = umm.GetType("UnityModManagerNet.UnityModManager", true);
            patcher.Patch(ReflectionAccess.Method(manager, "get_modsPath"),
                prefix: new HarmonyMethod(typeof(ModsPathBinder), nameof(GetModsPath)));
            patcher.Patch(ReflectionAccess.Method(manager, "set_modsPath", typeof(string)),
                prefix: new HarmonyMethod(typeof(ModsPathBinder), nameof(SetModsPath)));
            // Redirect Config in memory as soon as UMM reads it, before any wrong-path directory creation.
            var info = manager.GetNestedType("GameInfo", ReflectionAccess.All) ?? throw new TypeLoadException("UMM GameInfo missing.");
            patcher.Patch(ReflectionAccess.Method(info, "Load"),
                postfix: new HarmonyMethod(typeof(ModsPathBinder), nameof(ConfigLoaded)));
            patcher.Patch(ReflectionAccess.Method(manager, "Initialize"),
                postfix: new HarmonyMethod(typeof(ModsPathBinder), nameof(AfterInitialize)));
        }

        private static bool GetModsPath(ref string __result) { __result = modsPath; return false; }
        private static void SetModsPath(ref string __0) { __0 = modsPath; }
        private static void ConfigLoaded(object __result)
        {
            if (__result != null) ReflectionAccess.Set(__result, "ModsDirectory", modsPath);
        }
        private static void AfterInitialize(bool __result)
        {
            InitializedSuccessfully = __result;
            if (!__result) return;
            ReflectionAccess.Set(manager, "OldModsPath", modsPath);
            ReflectionAccess.Set(manager, "modsPath", modsPath);
        }
    }
}
