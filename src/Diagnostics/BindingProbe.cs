using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;

namespace WithUMM.Diagnostics
{
    internal static class BindingProbe
    {
        internal static Assembly ResolveHarmony(Assembly consumer)
        {
            if (consumer.IsDynamic || string.IsNullOrEmpty(consumer.Location) || !File.Exists(consumer.Location)) return null;
            using (var definition = AssemblyDefinition.ReadAssembly(consumer.Location))
            {
                // TypeRef metadata tokens belong to this exact consumer image. ResolveType measures
                // its bound assembly, unlike GetReferencedAssemblies (which only reports requested names).
                foreach (var type in definition.MainModule.GetTypeReferences())
                    if (type.Scope is AssemblyNameReference scope && scope.Name == "0Harmony")
                        return consumer.ManifestModule.ResolveType(type.MetadataToken.ToInt32()).Assembly;
            }
            return null;
        }
    }
}
