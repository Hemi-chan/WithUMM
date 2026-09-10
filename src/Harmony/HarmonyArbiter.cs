using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader.Resolver;
using Mono.Cecil;
using WithUMM.Diagnostics;

namespace WithUMM.Harmony
{
    internal sealed class HarmonyArbiter
    {
        private readonly BootTrace trace;
        private ResolverLease lease;
        internal Assembly Provider { get; private set; }
        internal HarmonyArbiter(BootTrace trace) { this.trace = trace; }

        internal void Prepare(string userData, string modsPath)
        {
            var hx = typeof(HarmonyLib.Harmony).Assembly;
            if (hx.GetName().Version != new Version(2, 10, 2, 0))
                throw new NotSupportedException("This build requires HarmonyX 2.10.2.0, found " + hx.FullName);
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "0Harmony" && a != hx))
                throw new InvalidOperationException("Another 0Harmony was loaded before WithUMM; restart without other UMM bridges.");
            var path = HarmonyProvider.Extract(userData);
            lease = new ResolverLease(MelonAssemblyResolver.GetAssemblyResolveInfo("0Harmony"), hx, HarmonyProvider.Version);
            try
            {
                Provider = HarmonyProvider.Load(path);
                HarmonyProvider.Verify(Provider, path);
                MelonAssemblyResolver.LoadInfoFromAssembly(Provider);
                lease.Commit(Provider);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) Observe(assembly);
                if (Directory.Exists(modsPath))
                    foreach (var directory in Directory.GetDirectories(modsPath)) ScanDirectory(directory);
                trace.Event("Harmony ready: UMM=" + Provider.FullName + " at " + Provider.Location + "; ML=" + hx.FullName);
            }
            catch { lease.Rollback(); throw; }
        }

        private void ScanDirectory(string directory)
        {
            // Read metadata only; do not execute mod code or follow directory links.
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var path in Directory.GetFiles(directory, "*.dll"))
            {
                try
                {
                    using (var assembly = AssemblyDefinition.ReadAssembly(path))
                        foreach (var reference in assembly.MainModule.AssemblyReferences)
                            if (reference.Name == "0Harmony") lease.RegisterReference(new AssemblyName(reference.FullName));
                }
                catch (BadImageFormatException) { }
                catch (Exception e) { trace.Warning("Metadata scan skipped " + path + ": " + e.Message); }
            }
            foreach (var subdirectory in Directory.GetDirectories(directory)) ScanDirectory(subdirectory);
        }

        internal void Observe(Assembly assembly)
        {
            if (lease == null) return;
            foreach (var reference in assembly.GetReferencedAssemblies()) lease.RegisterReference(reference);
        }

        internal void Audit()
        {
            if (Provider == null) return;
            var info = MelonAssemblyResolver.GetAssemblyResolveInfo("0Harmony");
            if (info.Override != null || info.GetVersionSpecific(HarmonyProvider.Version) != Provider)
                trace.Error("Harmony resolver changed after initialization; UMM compatibility is no longer assured. Restart without competing bridge plugins.");
        }

        internal void RollbackBeforeBoot()
        {
            lease?.Rollback();
            lease = null;
            Provider = null;
        }
    }
}
