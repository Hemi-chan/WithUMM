using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace WithUMM.Compat
{
    // Keep parsing and mod loading in UMM. Only stop its recursive sorter on unsafe input.
    internal static class ModGraphGuard
    {
        private static FieldInfo modEntries;
        private static Action<string> reportError;
        private static volatile bool sessionRejected;

        internal static void Install(global::HarmonyLib.Harmony harmony, Assembly umm, Action<string> error)
        {
            if (harmony == null) throw new ArgumentNullException(nameof(harmony));
            if (umm == null) throw new ArgumentNullException(nameof(umm));
            var manager = umm.GetType("UnityModManagerNet.UnityModManager", true);
            var entry = manager.GetNestedType("ModEntry", BindingFlags.Public);
            if (entry == null) throw new MissingMemberException(manager.FullName, "ModEntry");
            var dictionary = typeof(Dictionary<,>).MakeGenericType(typeof(string), entry);
            var sort = manager.GetMethod("TopoSort", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { dictionary }, null);
            var entries = manager.GetField("modEntries", BindingFlags.Public | BindingFlags.Static);
            if (sort == null || sort.ReturnType != typeof(void) || entries == null ||
                !typeof(IList).IsAssignableFrom(entries.FieldType))
                throw new MissingMemberException("Unsupported UMM TopoSort/modEntries contract.");
            var param = manager.GetNestedType("Param", BindingFlags.Public);
            var save = param?.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (save == null || save.ReturnType != typeof(void))
                throw new MissingMemberException("Unsupported UMM Param.Save contract.");
            // Validate the reflected member shape before claiming protection is installed.
            GetDependencyFields(entry, out _, out _);
            modEntries = entries;
            reportError = error;
            // UMM can save from the GUI or shutdown after we empty modEntries. Guard all Param.Save calls
            // before enabling graph rejection so existing Enabled/Hotkey preferences cannot be erased.
            harmony.Patch(save, prefix: new HarmonyMethod(typeof(ModGraphGuard).GetMethod(
                nameof(AllowParamSave), BindingFlags.NonPublic | BindingFlags.Static)));
            harmony.Patch(sort, prefix: new HarmonyMethod(typeof(ModGraphGuard).GetMethod(
                nameof(BeforeTopoSort), BindingFlags.NonPublic | BindingFlags.Static)));
        }

        internal static bool AllowParamSave() { return !sessionRejected; }

        internal static void RejectSession()
        {
            // Also protects a partially installed Injector that never reaches TopoSort.
            sessionRejected = true;
        }

        private static bool BeforeTopoSort(object __0)
        {
            return AllowSort(__0 as IDictionary, (IList)modEntries.GetValue(null), reportError);
        }

        internal static bool AllowSort(IDictionary mods, IList entries, Action<string> error)
        {
            string reason;
            try
            {
                if (!TryFindCycle(mods, out var cycle)) return true;
                reason = "UMM mod loading stopped: dependency cycle: " + cycle;
            }
            catch (Exception exception)
            {
                reason = "UMM mod loading stopped: dependency graph validation failed: " + exception.Message;
            }
            // Keep this latched until process restart; a subsequent check must not re-enable destructive saves.
            RejectSession();
            // UMM continues after TopoSort and loads everything in modEntries. Clearing is essential.
            entries.Clear();
            try { error?.Invoke(reason + "; Params.xml saving disabled for this session to preserve existing mod preferences."); }
            catch { /* Diagnostic callbacks must not turn a rejected graph into a game exception. */ }
            return false;
        }

        internal static bool TryFindCycle(IDictionary mods, out string cycle)
        {
            if (mods == null) throw new ArgumentNullException(nameof(mods));
            cycle = null;
            var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var memberCache = new Dictionary<Type, FieldInfo[]>();
            foreach (DictionaryEntry item in mods)
            {
                var id = item.Key as string;
                if (id == null || item.Value == null) throw new InvalidOperationException("UMM graph contains a null ID or entry.");
                var entryType = item.Value.GetType();
                if (!memberCache.TryGetValue(entryType, out var fields))
                {
                    GetDependencyFields(entryType, out var requirements, out var loadAfter);
                    fields = new[] { requirements, loadAfter };
                    memberCache.Add(entryType, fields);
                }
                var required = fields[0].GetValue(item.Value) as IDictionary;
                var optional = fields[1].GetValue(item.Value) as IEnumerable;
                if (required == null || optional == null) throw new InvalidOperationException("UMM dependency collections are unavailable.");
                var edges = new List<string>(required.Count);
                foreach (var value in required.Keys) AddEdge(edges, value);
                foreach (var value in optional) AddEdge(edges, value);
                graph.Add(id, edges);
            }

            // An explicit frame stack keeps the guard itself safe for very deep dependency graphs.
            var colors = new Dictionary<string, byte>(StringComparer.Ordinal);
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new List<Frame>();
            foreach (var root in graph.Keys)
            {
                if (colors.ContainsKey(root)) continue;
                colors.Add(root, 1);
                positions.Add(root, 0);
                stack.Add(new Frame(root));
                while (stack.Count != 0)
                {
                    var top = stack[stack.Count - 1];
                    var edges = graph[top.Id];
                    if (top.Next == edges.Count)
                    {
                        colors[top.Id] = 2;
                        positions.Remove(top.Id);
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }
                    var target = edges[top.Next++];
                    if (!graph.ContainsKey(target)) continue; // UMM handles missing required/optional IDs.
                    if (colors.TryGetValue(target, out var color))
                    {
                        if (color != 1) continue;
                        var path = new List<string>();
                        for (int i = positions[target]; i < stack.Count; i++) path.Add(stack[i].Id);
                        path.Add(target);
                        cycle = string.Join(" -> ", path);
                        return true;
                    }
                    colors.Add(target, 1);
                    positions.Add(target, stack.Count);
                    stack.Add(new Frame(target));
                }
            }
            return false;
        }

        private static void AddEdge(List<string> edges, object value)
        {
            var id = value as string;
            if (id == null) throw new InvalidOperationException("UMM dependency ID is not a string.");
            edges.Add(id); // Preserve UMM's exact case, whitespace and version parsing.
        }

        private static void GetDependencyFields(Type entry, out FieldInfo required, out FieldInfo optional)
        {
            required = entry.GetField("Requirements", BindingFlags.Instance | BindingFlags.Public);
            optional = entry.GetField("LoadAfter", BindingFlags.Instance | BindingFlags.Public);
            if (required == null || optional == null || !typeof(IDictionary).IsAssignableFrom(required.FieldType) ||
                !typeof(IEnumerable).IsAssignableFrom(optional.FieldType))
                throw new MissingMemberException(entry.FullName, "Requirements/LoadAfter");
        }

        private sealed class Frame
        {
            internal readonly string Id;
            internal int Next;
            internal Frame(string id) { Id = id; }
        }
    }
}
