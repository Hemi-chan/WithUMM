using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

namespace WithUMM.Harmony
{
    // H2 builds the inner wrapper; HX transforms that wrapper as the outer layer.
    // Registries and ordering remain engine-local, including reverse snapshots.
    internal static class ReplacementComposer
    {
        private sealed class Entry
        {
            internal ILHook Hook;
            internal DynamicMethodDefinition Body;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<MethodBase, Entry> Entries = new Dictionary<MethodBase, Entry>();
        private static Assembly installedProvider;
        private static PropertyInfo debugProperty;
        private static MethodInfo methodBuilderGenerate;
        private static Action<string> logger;

        internal static void Install(Assembly provider, global::HarmonyLib.Harmony hostPatcher, Action<string> log)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (hostPatcher == null) throw new ArgumentNullException(nameof(hostPatcher));
            lock (Gate)
            {
                if (installedProvider != null)
                {
                    if (installedProvider != provider) throw new InvalidOperationException("A different Harmony provider is already composed.");
                    return;
                }
                var dmd = provider.GetType("MonoMod.Utils.DynamicMethodDefinition", true);
                var generate = dmd.GetMethod("Generate", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(object) }, null);
                var generatorType = provider.GetType("MonoMod.Utils.DMDEmitMethodBuilderGenerator", true);
                var generatorBase = provider.GetType("MonoMod.Utils.DMDGenerator`1", true).MakeGenericType(generatorType);
                var build = generatorBase.GetMethod("Generate", BindingFlags.Public | BindingFlags.Static, null, new[] { dmd, typeof(object) }, null);
                var debug = dmd.GetProperty("Debug", BindingFlags.Public | BindingFlags.Instance);
                var detour = provider.GetType("HarmonyLib.PatchTools", true).GetMethod("DetourMethod", BindingFlags.NonPublic | BindingFlags.Static);
                if (generate == null || build == null || debug?.GetSetMethod(true) == null || detour == null)
                    throw new MissingMethodException("The provider does not expose the verified Harmony 2.3.6 DMD/DetourMethod contract.");
                var readablePrefix = typeof(ReplacementComposer).GetMethod(nameof(ReadableBody), BindingFlags.NonPublic | BindingFlags.Static);
                var detourPrefix = typeof(ReplacementComposer).GetMethod(nameof(Compose), BindingFlags.NonPublic | BindingFlags.Static);
                debugProperty = debug;
                methodBuilderGenerate = build;
                logger = log;
                try
                {
                    // Only this provider's generator changes; never set a process-wide MonoMod switch.
                    hostPatcher.Patch(generate, prefix: new HarmonyMethod(readablePrefix));
                    hostPatcher.Patch(detour, prefix: new HarmonyMethod(detourPrefix));
                    installedProvider = provider;
                    log?.Invoke("Harmony replacement composer installed: provider wrapper -> host ILHook. Cross-engine priority is not unified.");
                }
                catch
                {
                    hostPatcher.Unpatch(detour, detourPrefix);
                    hostPatcher.Unpatch(generate, readablePrefix);
                    installedProvider = null;
                    debugProperty = null;
                    methodBuilderGenerate = null;
                    logger = null;
                    throw;
                }
            }
        }

        private static bool ReadableBody(object __instance, object __0, ref MethodInfo __result)
        {
            // Select the provider's MethodBuilder generator directly. Debug=true also
            // requests symbol emission, which Unity's Mono cannot service without
            // Mono.CompilerServices.SymbolWriter. Keep its normal Postbuild path.
            debugProperty.SetValue(__instance, false, null);
            try
            {
                __result = (MethodInfo)methodBuilderGenerate.Invoke(null, new[] { __instance, __0 });
                if (__result == null) throw new InvalidOperationException("The provider MethodBuilder generator returned no method.");
                return false;
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static bool Compose(MethodBase method, MethodBase replacement)
        {
            if (method == null || replacement == null) throw new ArgumentNullException(method == null ? nameof(method) : nameof(replacement));
            if (method.GetMethodBody() == null)
                throw new NotSupportedException("Harmony composition requires managed IL: " + method);
            lock (Gate)
            {
                Entry previous;
                Entries.TryGetValue(method, out previous);
                var next = new Entry { Body = new DynamicMethodDefinition(replacement) };
                try
                {
                    next.Hook = new ILHook(method, context => CopyBody(next.Body.Definition, context), new ILHookConfig
                    {
                        ID = "WithUMM.Harmony2Baseline",
                        Priority = int.MaxValue,
                        Before = new[] { "*" },
                        ManualApply = true
                    });
                    previous?.Hook.Undo();
                    next.Hook.Apply();
                    if (!next.Hook.IsApplied) throw new InvalidOperationException("The host rejected the provider ILHook: " + method);
                    Entries[method] = next;
                }
                catch (Exception error)
                {
                    // Restore the previously working wrapper if an IL transformation rejects the update.
                    try { next.Hook?.Dispose(); } catch (Exception cleanup) { logger?.Invoke("Composition cleanup failed: " + cleanup); }
                    next.Body.Dispose();
                    try { previous?.Hook.Apply(); } catch (Exception rollback) { throw new AggregateException("Composition and rollback both failed for " + method, error, rollback); }
                    throw;
                }
                previous?.Hook.Dispose();
                previous?.Body.Dispose();
                return false;
            }
        }

        private static void CopyBody(MethodDefinition source, ILContext context)
        {
            var target = context.Method;
            // Retain DMD runtime reflection references. Re-importing them with Cecil
            // loses exact MethodBase identity and broke H2 reverse patching in the probe.
            target.Body = source.Body.Clone(target);
            foreach (var instruction in target.Body.Instructions)
            {
                // MonoMod's Body.Clone copies these operands by reference. HX's transpiler
                // reader keys locals by object identity, so remapping is mandatory.
                if (instruction.Operand is VariableDefinition local)
                    instruction.Operand = target.Body.Variables[local.Index];
                else if (instruction.Operand is ParameterDefinition parameter)
                    instruction.Operand = target.Parameters[parameter.Index];
            }
        }
    }
}
