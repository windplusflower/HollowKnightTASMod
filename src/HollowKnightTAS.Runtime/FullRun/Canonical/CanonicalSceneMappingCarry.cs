using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The original one-shot SceneManager mapping closure can survive native
    // scene destruction. Its flags still decide a PlayerData write.
    internal static class CanonicalSceneMappingCarry
    {
        private static readonly HashSet<string> LoggedWarnings = new HashSet<string>(StringComparer.Ordinal);
        internal static void WarnOnce(string key, string message)
        {
            lock (LoggedWarnings)
            {
                if (!LoggedWarnings.Add(key)) return;
            }
            try { Modding.Logger.LogWarn(message); }
            catch { Trace.TraceWarning(message); }
        }
        internal const string Root = "D/SceneManager-entry-mapping";
        internal const string DetachedPlayerRoot = "D/SceneManager-entry-mapping-detached-player";
        internal const string ModuleMvid = "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f";
        internal const string ClosureType = "SceneManager+<>c__DisplayClass41_0";
        internal const string HandlerName = "<Start>b__0";
        internal const int HandlerToken = 100667764;
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(Mapping), typeof(Mapping[]) };
        internal sealed class Data { public int Version = 1; public Mapping[] Items = Array.Empty<Mapping>(); }
        internal sealed class Mapping
        {
            public string MethodModuleMvid = "", MethodDeclaringType = "", MethodName = "";
            public int Order, MethodToken, MapZone;
            public bool ManualMapTrigger;
            public bool UsesCurrentPlayerData;
        }
        internal static bool IsEmptySourceGraph(CanonicalCarryGraphData graph)
        {
            InvalidDataException Invalid() => new InvalidDataException("Source empty scene mapping owner contract differs.");
            var roots = graph.Roots.Where(root => root.Binding == Root).ToArray();
            if (roots.Length != 1) throw Invalid();
            CanonicalCarryGraphData.Node Node(int id, Type type, string kind) {
                if (id < 1 || id > graph.Nodes.Length) throw Invalid();
                var node = graph.Nodes[id - 1];
                if (node == null || node.Id != id || node.Type != CanonicalCarryGraphCodec.Identity(type) || node.Kind != kind || node.Text != null)
                    throw Invalid();
                return node;
            }
            var dataType = CanonicalCarryGraphCodec.Identity(typeof(Data));
            var node = Node(roots[0].Node, typeof(Data), "object");
            if (node.Items != null || node.Fields == null || node.Fields.Length != 2
                || node.Fields[0].Name != dataType + "/Items" || node.Fields[1].Name != dataType + "/Version") throw Invalid();
            var version = node.Fields[1].Value; var items = node.Fields[0].Value;
            if (version.Kind != "scalar" || version.Type != CanonicalCarryGraphCodec.Identity(typeof(int)) || version.Text != "1" || version.Reference != 0
                || items.Kind != "node" || items.Type != CanonicalCarryGraphCodec.Identity(typeof(Mapping[])) || items.Text != null) throw Invalid();
            var array = Node(items.Reference, typeof(Mapping[]), "array");
            if (array.Fields != null || array.Items == null || array.Items.Length > 1) throw Invalid();
            if (array.Items.Length != 0) return false; // Preserve the original pending/B1 binding path.
            if (graph.Roots.Any(root => root.Binding == DetachedPlayerRoot)) throw Invalid();
            return true;
        }
        internal static Data Copy(Data data)
        {
            Validate(data);
            var result = new Data { Version = data.Version, Items = new Mapping[data.Items.Length] };
            for (var index = 0; index < data.Items.Length; index++) {
                var item = data.Items[index]; result.Items[index] = new Mapping { Order = item.Order, MethodModuleMvid = item.MethodModuleMvid,
                    MethodDeclaringType = item.MethodDeclaringType, MethodName = item.MethodName, MethodToken = item.MethodToken,
                    MapZone = item.MapZone, ManualMapTrigger = item.ManualMapTrigger, UsesCurrentPlayerData = item.UsesCurrentPlayerData };
            }
            return result;
        }
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var root = view.Root(Root);
            return Copy(new Data { Version = (int)view.Field(root, typeof(Data), nameof(Data.Version))!,
                Items = (Mapping[])view.Field(root, typeof(Data), nameof(Data.Items))! });
        }
        internal static void Validate(Data data)
        {
            // Other callback families and multiple pending mappings need an
            // explicit original ownership contract; never infer one by type.
            if (data == null || data.Version != 1 || data.Items == null || data.Items.Length > 1)
                throw new InvalidDataException("Scene entry mapping chain is outside the registered single original callback.");
            for (var index = 0; index < data.Items.Length; index++) {
                var item = data.Items[index];
                if (item == null || item.Order != index || string.IsNullOrEmpty(item.MethodDeclaringType) || string.IsNullOrEmpty(item.MethodName)
                    || item.MethodToken <= 0 || !Guid.TryParseExact(item.MethodModuleMvid, "D", out var mvid) || mvid == Guid.Empty)
                    throw new InvalidDataException("Scene entry mapping original method or order differs.");
                if (item.MethodModuleMvid != ModuleMvid || item.MethodDeclaringType != ClosureType
                    || item.MethodName != HandlerName || item.MethodToken != HandlerToken)
                    WarnOnce("SceneMappingCarry:" + item.MethodModuleMvid + ":" + item.MethodDeclaringType + ":" + item.MethodName + ":" + item.MethodToken,
                        "[HKTAS] Scene entry mapping metadata differs from pinned original (continuing): mvid=" + item.MethodModuleMvid
                        + ";type=" + item.MethodDeclaringType + ";method=" + item.MethodName + ";token=" + item.MethodToken);
            }
        }
    }
}
