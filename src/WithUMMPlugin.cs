using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using MelonLoader.Utils;
using WithUMM.Boot;
using WithUMM.Diagnostics;
using WithUMM.Harmony;

[assembly: MelonInfo(typeof(WithUMM.WithUMMPlugin), "WithUMM", "0.1.0", "hemi")]
[assembly: MelonPriority(-10000)]

namespace WithUMM
{
    public sealed class WithUMMPlugin : MelonPlugin
    {
        private BootTrace trace;
        private WithUMMConfig config;
        private HarmonyArbiter arbiter;
        private UmmBootstrapper bootstrapper;
        private readonly InjectorGate gate = new InjectorGate();
        private HarmonyLib.Harmony patcher;
        private UmmInstallation installation;
        private bool inPreInitialization;
        private bool ready;
        private bool failed;
        private bool coreObserved;

        public override void OnPreInitialization()
        {
            trace = new BootTrace(LoggerInstance);
            trace.Event("Phase A: OnPreInitialization; cwd=" + Environment.CurrentDirectory);
            inPreInitialization = true;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            try
            {
                config = WithUMMConfig.Load();
                if (config.DiagnosticsOnly) { trace.Event("DiagnosticsOnly enabled; observing without binding or boot changes."); return; }
                if (!MelonEnvironment.IsMonoRuntime || Type.GetType("Mono.Runtime") == null)
                    throw new NotSupportedException("WithUMM requires a Unity Mono player; IL2CPP is unsupported.");
                var existing = AppDomain.CurrentDomain.GetAssemblies();
                if (existing.Any(a => a.GetName().Name == "UnityEngine.CoreModule" || a.GetName().Name == "UnityModManager"))
                    throw new InvalidOperationException("Unity/UMM was loaded before WithUMM Phase A. Early startup ownership cannot be established.");
                if (existing.Any(a => a.GetName().Name == "UMMBridge" || a.GetName().Name == "UMMBridgeFix"))
                    throw new InvalidOperationException("UMMBridge/UMMBridgeFix and WithUMM cannot own UMM startup together. Remove the other bridge plugins before using WithUMM.");
                var root = MelonEnvironment.MelonBaseDirectory;
                var mods = Path.Combine(root, "Mods");
                installation = UmmLocator.Find(root, MelonEnvironment.GameExecutablePath, config.UmmDirectory);
                trace.Event("Installed UMM=" + installation.AssemblyPath + "; Mods=" + mods);
                arbiter = new HarmonyArbiter(trace);
                arbiter.Prepare(MelonEnvironment.UserDataDirectory, mods);
                if (failed) throw new InvalidOperationException("A nested assembly-load failure interrupted Phase A.");
                patcher = new HarmonyLib.Harmony("WithUMM.Infrastructure");
                ReplacementComposer.Install(arbiter.Provider, patcher, trace.Event);
                if (failed) throw new InvalidOperationException("A nested assembly-load failure interrupted composer installation.");
                bootstrapper = new UmmBootstrapper(installation, patcher, arbiter.Provider, trace, mods, config.MirrorLog);
                ready = true;
                var legacy = Path.Combine(root, "UMMMods");
                if (Directory.Exists(legacy) && Directory.GetDirectories(legacy).Length > 0)
                    trace.Warning("UMMMods contains folders. WithUMM reads Mods/<mod>/Info.json; move the desired UMM mod folders to Mods before the next launch.");
                trace.Event("Phase A ready; no UMM or Unity API was initialized.");
            }
            catch (Exception e) { Fail("Phase A", e); }
            finally { inPreInitialization = false; }
        }

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                var assembly = args.LoadedAssembly;
                var name = assembly.GetName().Name;
                arbiter?.Observe(assembly);
                if (name == "UnityEngine.CoreModule")
                {
                    coreObserved = true;
                    trace.Event("CoreModule AssemblyLoad; during-phase-A=" + inPreInitialization);
                    if (inPreInitialization && config != null && !config.DiagnosticsOnly)
                        throw new InvalidOperationException("CoreModule loaded during Phase A; refusing early UMM initialization.");
                    if (ready && !failed)
                        trace.Event(gate.Install(assembly, patcher) ? "Native UMM Starter gated in memory." : "No native UMM Starter; WithUMM will boot installed UMM.");
                }
                if (name == "Assembly-CSharp" || name == "UnityModManager") trace.Event(name + " AssemblyLoad");
                if (ready && !failed && name == installation.StartingAssembly)
                {
                    if (!coreObserved) throw new InvalidOperationException("Starting assembly arrived before CoreModule; no unsafe Unity initialization attempted.");
                    trace.Event("Phase B: configured starting assembly loaded");
                    bootstrapper.Boot();
                }
            }
            catch (Exception e)
            {
                // Let the outer preparation frame finish unwinding before restoring the resolver.
                if (inPreInitialization) { failed = true; trace.Failure("AssemblyLoad during Phase A", e); }
                else Fail("AssemblyLoad", e);
            }
        }

        private void Fail(string phase, Exception e)
        {
            failed = true;
            ready = false;
            trace.Failure(phase, e);
            if (!InjectorGate.WasSuppressed && !(bootstrapper?.Attempted ?? false))
            {
                try
                {
                    patcher?.UnpatchSelf();
                    arbiter?.RollbackBeforeBoot();
                    gate.Release();
                    trace.Warning("WithUMM setup was rolled back before UMM startup; the original loader path remains available.");
                }
                catch (Exception cleanup) { trace.Failure("Early setup rollback failed; restart required", cleanup); }
            }
            else
            {
                bootstrapper?.Abort();
                trace.Error("UMM startup is already owned or partially initialized. The native gate remains closed; restart after correcting the reported error.");
            }
        }

        public override void OnApplicationEarlyStart() { trace?.Event("Phase C: MelonLoader OnApplicationEarlyStart"); }
        public override void OnPreModsLoaded() { trace?.Event("MelonLoader OnPreModsLoaded"); arbiter?.Audit(); }
        public override void OnLateInitializeMelon()
        {
            trace?.Event("MelonLoader OnLateInitializeMelon; failed=" + failed + "; native-gate=" + gate.Installed);
            arbiter?.Audit();
            bootstrapper?.Report("MelonLoader late initialization");
            PatchConflictScanner.Report(arbiter?.Provider, trace);
            if (ready && bootstrapper != null && !bootstrapper.Attempted)
                trace.Error("Configured UMM starting assembly was not observed. No late Start() fallback was forced after the game's startup point.");
        }

        public override void OnDeinitializeMelon()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            trace?.Event("WithUMM shutdown. UMM retains its own save/UI lifecycle.");
        }
    }
}
