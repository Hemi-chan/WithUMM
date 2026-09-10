using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader.Resolver;

namespace WithUMM.Harmony
{
    internal sealed class ResolverLease
    {
        private readonly AssemblyResolveInfo info;
        private readonly Assembly harmonyX;
        private readonly Assembly previousOverride;
        private readonly Assembly previousFallback;
        private readonly Dictionary<Version, Assembly> versions;
        private readonly Dictionary<Version, Assembly> saved = new Dictionary<Version, Assembly>();
        private readonly HashSet<Version> introduced = new HashSet<Version>();
        private readonly Version providerVersion;
        private Assembly provider;

        internal ResolverLease(AssemblyResolveInfo info, Assembly harmonyX, Version providerVersion)
        {
            this.info = info ?? throw new ArgumentNullException(nameof(info));
            this.harmonyX = harmonyX ?? throw new ArgumentNullException(nameof(harmonyX));
            this.providerVersion = providerVersion;
            versions = typeof(AssemblyResolveInfo).GetField("Versions", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(info) as Dictionary<Version, Assembly>
                ?? throw new NotSupportedException("MelonLoader resolver contract changed: Versions dictionary missing.");
            previousOverride = info.Override;
            previousFallback = info.Fallback;
            // Remove both forced routes before loading and registering the separate provider.
            Set(providerVersion, null);
            info.Override = null;
            info.Fallback = null;
        }

        internal void Commit(Assembly assembly)
        {
            provider = assembly ?? throw new ArgumentNullException(nameof(assembly));
            Set(providerVersion, provider);
            Set(harmonyX.GetName().Version, harmonyX);
            info.Fallback = harmonyX;
        }

        internal void RegisterReference(AssemblyName reference)
        {
            var v = reference.Version;
            if (provider != null && reference.Name == "0Harmony" && v != null && v.Major == 2 && v.Minor <= 4)
                Set(v, provider);
        }

        private void Set(Version version, Assembly assembly)
        {
            lock (versions)
            {
                if (!saved.ContainsKey(version) && !introduced.Contains(version))
                {
                    if (versions.TryGetValue(version, out var previous)) saved.Add(version, previous);
                    else introduced.Add(version);
                }
                versions[version] = assembly;
            }
        }

        internal void Rollback()
        {
            lock (versions)
            {
                foreach (var version in introduced) versions.Remove(version);
                foreach (var pair in saved) versions[pair.Key] = pair.Value;
                introduced.Clear(); saved.Clear();
            }
            info.Override = previousOverride;
            info.Fallback = previousFallback;
            provider = null;
        }
    }
}
