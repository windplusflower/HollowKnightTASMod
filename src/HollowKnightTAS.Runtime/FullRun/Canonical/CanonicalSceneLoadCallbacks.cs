using System;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalSceneLoadCallbacks
    {
        internal const string Root = "D/SceneLoad-callbacks";
        internal const string ClosureType = "GameManager+<>c__DisplayClass174_0";
        internal static readonly string[] Fields = { "ActivationComplete", "BossLoaded", "Complete", "FetchComplete", "Finish", "StartCalled", "WillActivate" };
        internal static readonly int[] Tokens = { 100667494, 0, 100667495, 100667492, 100667496, 0, 100667493 };
        internal static readonly string[] Methods = { "<BeginSceneTransitionRoutine>b__2", "", "<BeginSceneTransitionRoutine>b__3",
            "<BeginSceneTransitionRoutine>b__0", "<BeginSceneTransitionRoutine>b__4", "", "<BeginSceneTransitionRoutine>b__1" };
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(Slot), typeof(Slot[]) };
        internal static string SchemaIdentity => ClosureType + ":" + CanonicalSceneMappingCarry.ModuleMvid + ":" +
            string.Join(";", Fields.Select((field, index) => field + "=" + Tokens[index] + "/" + Methods[index]))
            + ":retained-exact-factory-diagnostic-suffix-v1:LoadingGameplayOracle,LoadingBoundaryProbe:max2";
        internal sealed class Data { public int Version = 1; public string SourceScene = ""; public Slot[] Slots = Array.Empty<Slot>(); }
        internal sealed class Slot
        {
            public string Field = "", ModuleMvid = "", DeclaringType = "", Method = "";
            public int Token, SharedClosure = -1;
        }
        internal static Data Copy(Data source)
        {
            if (source == null || source.Version != 1 || string.IsNullOrEmpty(source.SourceScene) || source.SourceScene.Length > 4096
                || source.Slots == null || source.Slots.Length != Fields.Length)
                throw new InvalidDataException("Original scene loader callback packet differs.");
            for (var index = 0; index < Fields.Length; index++) {
                var item = source.Slots[index]; var present = Tokens[index] != 0;
                if (item == null || item.Field != Fields[index] || item.Token != Tokens[index] || item.Method != Methods[index]
                    || item.SharedClosure != (present ? 0 : -1) || item.ModuleMvid != (present ? CanonicalSceneMappingCarry.ModuleMvid : "")
                    || item.DeclaringType != (present ? ClosureType : ""))
                    throw new InvalidDataException("Unregistered scene loader callback method, order or shared target alias.");
            }
            return new Data { Version = source.Version, SourceScene = source.SourceScene, Slots = source.Slots.Select(item => new Slot {
                Field = item.Field, Token = item.Token, ModuleMvid = item.ModuleMvid, DeclaringType = item.DeclaringType,
                Method = item.Method, SharedClosure = item.SharedClosure }).ToArray() };
        }
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var root = view.Root(Root);
            return Copy(new Data { Version = (int)view.Field(root, typeof(Data), nameof(Data.Version))!,
                SourceScene = (string)view.Field(root, typeof(Data), nameof(Data.SourceScene))!,
                Slots = (Slot[])view.Field(root, typeof(Data), nameof(Data.Slots))! });
        }
        internal static void RequireDiagnosticSuffix(Delegate[] actual, Delegate[] registered, int originalCount)
        {
            if (actual == null || registered == null || originalCount < 0 || originalCount > 1 || registered.Length > 2
                || actual.Length != originalCount + registered.Length
                || registered.Where((observer, ordinal) => observer == null || !ReferenceEquals(observer, actual[originalCount + ordinal])).Any())
                throw new InvalidDataException("Scene loader callback order or exact registered diagnostic listeners differ.");
        }
    }
}
