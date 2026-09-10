using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using WithUMM.Compat;
using WithUMM.Diagnostics;

namespace WithUMM.Boot
{
    internal sealed class UmmBootstrapper
    {
        private readonly UmmInstallation installation;
        private readonly HarmonyLib.Harmony patcher;
        private readonly Assembly provider;
        private readonly BootTrace trace;
        private readonly string modsPath;
        private readonly bool mirrorLog;
        private Type manager;
        private Exception injectorError;
        private bool aborted;
        internal bool Attempted { get; private set; }
        internal bool StartReturned { get; private set; }
        private static UmmBootstrapper current;

        internal UmmBootstrapper(UmmInstallation installation, HarmonyLib.Harmony patcher, Assembly provider, BootTrace trace, string modsPath, bool mirrorLog)
        {
            this.installation = installation;
            this.patcher = patcher;
            this.provider = provider;
            this.trace = trace;
            this.modsPath = modsPath;
            this.mirrorLog = mirrorLog;
        }

        internal void Boot()
        {
            if (Attempted) return;
            Attempted = true;
            var umm = Assembly.LoadFrom(installation.AssemblyPath);
            var boundHarmony = BindingProbe.ResolveHarmony(umm);
            if (boundHarmony != provider)
                throw new InvalidOperationException("UMM TypeRef bound to " + boundHarmony?.FullName + " instead of the bundled provider.");
            trace.Event("Verified UMM TypeRef binding: " + boundHarmony.FullName + " at " + boundHarmony.Location);
            manager = umm.GetType("UnityModManagerNet.UnityModManager", true);
            if (ReflectionAccess.Get(manager, "initialized") is bool initialized && initialized)
                throw new InvalidOperationException("UMM initialized before WithUMM acquired startup ownership; remove competing UMM bootstrap plugins and restart.");
            if (mirrorLog) UmmLogMirror.Install(patcher, umm, trace);
            ModsPathBinder.Install(patcher, umm, modsPath);
            ModGraphGuard.Install(patcher, umm, trace.Error);
            current = this;
            patcher.Patch(ReflectionAccess.Method(manager, "_Start"),
                postfix: new HarmonyMethod(typeof(UmmBootstrapper), nameof(AfterStart)),
                finalizer: new HarmonyMethod(typeof(UmmBootstrapper), nameof(StartException)));
            var injector = umm.GetType("UnityModManagerNet.Injector", true);
            foreach (var callback in new[] { "Prefix_Start", "Postfix_Start", "Prefix_Show", "Postfix_Show" })
                patcher.Patch(ReflectionAccess.Method(injector, callback),
                    prefix: new HarmonyMethod(typeof(UmmBootstrapper), nameof(AllowInjectorCallback)));
            // _Run is called by the original public wrapper too; observing its finalizer reveals swallowed errors.
            patcher.Patch(ReflectionAccess.Method(injector, "_Run", typeof(bool)),
                finalizer: new HarmonyMethod(typeof(UmmBootstrapper), nameof(InjectorException)));
            trace.Event("Calling installed Injector.Run(false) at " + installation.StartingAssembly + " load");
            ReflectionAccess.Method(injector, "Run", typeof(bool)).Invoke(null, new object[] { false });
            if (injectorError != null) throw new InvalidOperationException("UMM Injector._Run failed after partial initialization.", injectorError);
            if (!ModsPathBinder.InitializedSuccessfully)
                throw new InvalidOperationException("UMM Initialize did not finish successfully. Its early initialized flag is not a success result; no blind retry will be attempted.");
            VerifyStartupPatches(injector);
            trace.Event("UMM initialization returned; waiting for configured startup method and UI. This is not a mod-load success claim.");
        }

        private void VerifyStartupPatches(Type injector)
        {
            var configuration = ReflectionAccess.Get(manager, "Config") ?? throw new InvalidOperationException("UMM Config was not loaded.");
            var starting = (string)ReflectionAccess.Get(configuration, "StartingPoint");
            var uiStarting = (string)ReflectionAccess.Get(configuration, "UIStartingPoint");
            var entry = (string)ReflectionAccess.Get(configuration, "EntryPoint");
            if (starting == entry)
            {
                if (!StartReturned) throw new InvalidOperationException("UMM immediate startup did not return.");
            }
            else VerifyPatch(injector, starting, "Start");
            if (!string.IsNullOrEmpty(uiStarting) && uiStarting != starting) VerifyPatch(injector, uiStarting, "Show");
        }

        private void VerifyPatch(Type injector, string entry, string callback)
        {
            var lookup = ReflectionAccess.Method(injector, "TryGetEntryPoint", typeof(string), typeof(Type).MakeByRefType(),
                typeof(MethodInfo).MakeByRefType(), typeof(string).MakeByRefType());
            var arguments = new object[] { entry, null, null, null };
            if (!(bool)lookup.Invoke(null, arguments)) throw new MissingMethodException("UMM entry point could not be resolved: " + entry);
            var original = (MethodInfo)arguments[2];
            var before = (string)arguments[3] == "before";
            var harmony = provider.GetType("HarmonyLib.Harmony", true);
            var patches = ReflectionAccess.Method(harmony, "GetPatchInfo", typeof(MethodBase)).Invoke(null, new object[] { original });
            var list = patches == null ? null : ReflectionAccess.Get(patches, before ? "Prefixes" : "Postfixes") as IEnumerable;
            var expected = (before ? "Prefix_" : "Postfix_") + callback;
            if (list != null)
                foreach (var patch in list)
                {
                    var method = ReflectionAccess.Get(patch, "PatchMethod") as MethodInfo;
                    if ((string)ReflectionAccess.Get(patch, "owner") == "UnityModManager" && method?.DeclaringType == injector && method.Name == expected) return;
                }
            throw new InvalidOperationException("UMM returned without installing " + expected + " on " + entry + ". No late retry was forced.");
        }

        private static void AfterStart()
        {
            if (current == null) return;
            current.StartReturned = true;
            current.Report("UMM _Start returned");
        }
        private static Exception StartException(Exception __exception)
        {
            if (__exception != null)
            {
                current?.Abort();
                current?.trace.Failure("UMM _Start threw", __exception);
            }
            return __exception;
        }

        private static bool AllowInjectorCallback() => current != null && !current.aborted;

        internal void Abort()
        {
            aborted = true;
            ModGraphGuard.RejectSession();
            trace.Error("UMM startup failed: later Start/Show callbacks and Params.xml saving are blocked for this session to preserve existing preferences.");
        }
        private static Exception InjectorException(Exception __exception)
        {
            if (__exception != null && current != null)
            {
                current.injectorError = __exception;
                current.trace.Failure("UMM Injector._Run threw", __exception);
            }
            return __exception;
        }

        internal void Report(string phase)
        {
            if (manager == null) return;
            try
            {
                trace.Event(phase + ": initialized=" + ReflectionAccess.Get(manager, "initialized") +
                    ", started=" + ReflectionAccess.Get(manager, "started") + ", start-returned=" + StartReturned);
                var entries = ReflectionAccess.Get(manager, "modEntries") as IEnumerable;
                if (entries == null) return;
                var count = 0;
                foreach (var entry in entries)
                {
                    count++;
                    var info = ReflectionAccess.Get(entry, "Info");
                    var assembly = ReflectionAccess.Get(entry, "Assembly") as Assembly;
                    var refs = assembly == null ? "no loaded assembly" : string.Join(", ", assembly.GetReferencedAssemblies()
                        .Where(a => a.Name == "0Harmony").Select(a => a.FullName));
                    var actual = assembly == null ? null : BindingProbe.ResolveHarmony(assembly);
                    trace.Event("mod=" + ReflectionAccess.Get(info, "Id") + ", loaded=" + ReflectionAccess.Get(entry, "Loaded") +
                        ", active=" + ReflectionAccess.Get(entry, "Active") + ", load-error=" + ReflectionAccess.Get(entry, "ErrorOnLoading") +
                        ", Harmony AssemblyRef=" + refs + ", actual-binding=" + (actual?.FullName ?? "not probed/no TypeRef") + ", location=" + assembly?.Location);
                }
                trace.Event("UMM entries=" + count + "; UI/settings/gameplay still require in-game verification.");
            }
            catch (Exception e) { trace.Failure("UMM status collection", e); }
        }
    }
}
