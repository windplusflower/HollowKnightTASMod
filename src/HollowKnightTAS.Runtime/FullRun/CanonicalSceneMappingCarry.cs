using System;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The original one-shot SceneManager mapping closure can survive native
    // scene destruction. Its flags still decide a PlayerData write.
    internal static class CanonicalSceneMappingCarry
    {
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
                if (item == null || item.Order != index || item.MethodModuleMvid != ModuleMvid || item.MethodDeclaringType != ClosureType
                    || item.MethodName != HandlerName || item.MethodToken != HandlerToken)
                    throw new InvalidDataException("Scene entry mapping original method or order differs.");
            }
        }
    }
}
