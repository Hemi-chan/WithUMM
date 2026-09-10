using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;

namespace WithUMM.Boot
{
    internal sealed class UmmInstallation
    {
        internal string Directory;
        internal string AssemblyPath => Path.Combine(Directory, "UnityModManager.dll");
        internal string StartingAssembly;
    }

    internal static class UmmLocator
    {
        internal static UmmInstallation Find(string gameRoot, string executable, string configured)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathRooted(configured)) throw new ArgumentException("UmmDirectory must be an absolute path.");
                candidates.Add(Path.GetFullPath(configured));
            }
            else
            {
                if (System.IO.Directory.Exists(gameRoot))
                    foreach (var data in System.IO.Directory.GetDirectories(gameRoot, "*_Data"))
                        candidates.Add(Path.Combine(data, "Managed", "UnityModManager"));
                // macOS layouts: the executable may live inside .app/Contents/MacOS.
                var ancestor = new DirectoryInfo(Path.GetDirectoryName(executable) ?? gameRoot);
                for (var depth = 0; ancestor != null && depth < 4; depth++, ancestor = ancestor.Parent)
                {
                    candidates.Add(Path.Combine(ancestor.FullName, "Resources", "Data", "Managed", "UnityModManager"));
                    candidates.Add(Path.Combine(ancestor.FullName, "Contents", "Resources", "Data", "Managed", "UnityModManager"));
                }
                candidates.Add(Path.Combine(gameRoot, "Contents", "Resources", "Data", "Managed", "UnityModManager"));
            }
            var comparer = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var valid = candidates.Distinct(comparer).Where(p => File.Exists(Path.Combine(p, "UnityModManager.dll"))).ToArray();
            if (valid.Length == 0) throw new FileNotFoundException("Installed UnityModManager.dll not found. Set [WithUMM].UmmDirectory to its Managed/UnityModManager directory.");
            if (valid.Length > 1) throw new InvalidOperationException("Multiple UMM installations found; set UmmDirectory explicitly: " + string.Join(", ", valid));
            var result = new UmmInstallation { Directory = valid[0] };
            var name = AssemblyName.GetAssemblyName(result.AssemblyPath);
            if (name.Name != "UnityModManager" || name.Version != new Version(0, 32, 4, 0))
                throw new NotSupportedException("Unsupported UMM contract: " + name.FullName + ". This build was verified against 0.32.4.0.");
            using (var reader = XmlReader.Create(Path.Combine(result.Directory, "Config.xml"),
                       new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                var xml = new XmlDocument { XmlResolver = null };
                xml.Load(reader);
                var start = xml.SelectSingleNode("/Config/StartingPoint")?.InnerText;
                result.StartingAssembly = AssemblyFromEntryPoint(start);
            }
            return result;
        }

        internal static string AssemblyFromEntryPoint(string entry)
        {
            if (string.IsNullOrEmpty(entry) || entry[0] != '[' || entry.IndexOf(']') < 2)
                throw new InvalidDataException("Config.xml requires a StartingPoint with [Assembly.dll].");
            return Path.GetFileNameWithoutExtension(entry.Substring(1, entry.IndexOf(']') - 1));
        }
    }
}
