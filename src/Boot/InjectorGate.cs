using System;
using System.Reflection;
using HarmonyLib;

namespace WithUMM.Boot
{
    internal sealed class InjectorGate
    {
        private static bool allowOriginal = true;
        internal bool Installed { get; private set; }
        internal static bool WasSuppressed { get; private set; }

        internal bool Install(Assembly coreModule, HarmonyLib.Harmony patcher)
        {
            var starter = coreModule.GetType("UnityModManagerNet.Injection.UnityModManagerStarter");
            if (starter == null) return false;
            var method = ReflectionAccess.Method(starter, "Start", Type.EmptyTypes);
            patcher.Patch(method, prefix: new HarmonyMethod(typeof(InjectorGate), nameof(BeforeInjectedStart)));
            allowOriginal = false;
            Installed = true;
            return true;
        }

        private static bool BeforeInjectedStart()
        {
            if (!allowOriginal) WasSuppressed = true;
            return allowOriginal;
        }

        internal void Release() { allowOriginal = true; }
    }
}
