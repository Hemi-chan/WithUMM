using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace WithUMM.Harmony
{
    internal static class HarmonyProvider
    {
        internal static readonly Version Version = new Version(2, 3, 6, 0);

        internal static Assembly Load(string path)
        {
            // Unity Mono unifies LoadFrom with the previously loaded weak-name HarmonyX,
            // even after a versioned search hook returns null. Only this provider uses LoadFile;
            // UMM and all original mod loads retain their normal LoadFrom identity semantics.
            return Assembly.LoadFile(Path.GetFullPath(path));
        }

        internal static string Extract(string userData)
        {
            byte[] bytes;
            using (var input = typeof(HarmonyProvider).Assembly.GetManifestResourceStream("WithUMM.Harmony236.Core133.dll"))
            {
                if (input == null) throw new InvalidDataException("The pinned Harmony provider resource is missing.");
                using (var output = new MemoryStream()) { input.CopyTo(output); bytes = output.ToArray(); }
            }
            string hash;
            using (var sha = SHA256.Create()) hash = Hex(sha.ComputeHash(bytes));
            var directory = Path.Combine(userData, "WithUMM", "Runtime", hash);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, "0Harmony.dll");
            if (!File.Exists(destination))
            {
                var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(temporary, bytes);
                    try { File.Move(temporary, destination); }
                    catch (IOException) { if (!File.Exists(destination)) throw; }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(destination))
                if (Hex(sha.ComputeHash(stream)) != hash)
                    throw new InvalidDataException("WithUMM provider cache hash mismatch: " + destination);
            return destination;
        }

        internal static void Verify(Assembly assembly, string expectedPath)
        {
            if (assembly.GetName().Name != "0Harmony" || assembly.GetName().Version != Version)
                throw new InvalidOperationException("Provider load resolved to the wrong Harmony: " + assembly.FullName);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetFullPath(assembly.Location), Path.GetFullPath(expectedPath), comparison))
                throw new InvalidOperationException("Provider load returned an unexpected assembly from " + assembly.Location);
            foreach (var type in new[] { "HarmonyLib.MethodPatcher", "HarmonyLib.Emitter", "HarmonyLib.MethodCopier", "HarmonyLib.HarmonySharedState", "MonoMod.Core.Platforms.Architectures.Arm64Arch" })
                if (assembly.GetType(type) == null) throw new TypeLoadException("Pinned provider contract missing: " + type);
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
