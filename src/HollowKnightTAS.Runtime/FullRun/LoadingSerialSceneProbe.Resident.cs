using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingSerialSceneProbe
    {
        private IEnumerator? residentBeginRoutine;
        internal static readonly IReadOnlyDictionary<string, string> ResidentFieldOwnership = ResidentProcessSlots.Ownership(
            "D:sourceName",
            "Dependency:transition,checkpointLoadInfo,source,staging,checkpointTransitionRoutine",
            "Execution:beginHook,unloadHook,transitionHook,lightingHook,report,sourceUnloaded,awaitingBoundary,observedBoundary,protectedClockCanonScope,protectedSourceCharmDerivationConsumed,protectedSourceGeoDerivationConsumed,protectedSourceMarkerDerivationConsumed,protectedSourceMapKeyDerivationConsumed,retiredSourceOwners,ordinal,rows,failure,protectedCompletedCoroutineDerivationConsumed,protectedSourceBorderDerivationConsumed,protectedFinalVisualVisibilityConsumed,protectedHudControlsConsumed,checkpointTransitionFinished,checkpointBossBindings,residentBeginRoutine");

        internal sealed class ResidentLoaderData
        {
            public ResidentProcessSlots.Data Owner = null!;
            public ResidentIteratorField[] Iterator = Array.Empty<ResidentIteratorField>();
            public bool FrameObservation;
            public string SceneDependencies = "";
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class ResidentIteratorField
        {
            public int Token, Attributes;
            public string Name = "", Value = "";
            public ResidentManagedTypeCatalog.TypeReference DeclaringType = null!, ValueType = null!;
        }

        // Captured by the executing native frame callback, before releasing the
        // parked iterator. No legacy experiment/environment flag grants access.
        internal static ResidentLoaderBoundary CaptureResidentBoundary()
            => new ResidentLoaderBoundary(instance ?? throw new InvalidOperationException("Resident serial loader is absent."));

        internal sealed class ResidentLoaderBoundary
        {
            private readonly LoadingSerialSceneProbe owner;
            private readonly SceneLoad load;
            private readonly GameManager.SceneLoadInfo info;
            private readonly IEnumerator begin, transition;
            private readonly int ordinal, thread, stagingHandle;
            private readonly bool wasObserved;
            private readonly ResidentProcessSlots slots;
            private readonly Dictionary<FieldInfo, object?> iteratorValues = new Dictionary<FieldInfo, object?>();
            private readonly string iteratorManifest;

            private static bool IsControlPlane(string? assembly)
                => assembly == "HollowKnightTAS" || assembly == "HollowKnightTAS.Core" || assembly == "HollowKnightTAS.ClockPayload";

            private static string CanonicalIteratorJson(ResidentIteratorField[] fields)
            {
                ResidentManagedTypeCatalog.TypeReference NormalizeType(ResidentManagedTypeCatalog.TypeReference? tr)
                {
                    if (tr == null) return null!;
                    var isControl = IsControlPlane(tr.Assembly);
                    return new ResidentManagedTypeCatalog.TypeReference {
                        Kind = tr.Kind,
                        Name = tr.Name,
                        Assembly = tr.Assembly,
                        Module = isControl ? "" : tr.Module,
                        Token = isControl ? 0 : tr.Token,
                        Rank = tr.Rank,
                        SzArray = tr.SzArray,
                        Position = tr.Position,
                        Element = tr.Element != null ? NormalizeType(tr.Element) : null,
                        Arguments = tr.Arguments != null ? tr.Arguments.Select(NormalizeType).ToArray() : Array.Empty<ResidentManagedTypeCatalog.TypeReference>()
                    };
                }

                var copy = fields.Select(f => new ResidentIteratorField {
                    Token = IsControlPlane(f.DeclaringType?.Assembly) ? 0 : f.Token,
                    Attributes = f.Attributes,
                    Name = f.Name,
                    Value = f.Value,
                    DeclaringType = NormalizeType(f.DeclaringType),
                    ValueType = NormalizeType(f.ValueType)
                }).ToArray();
                return JsonConvert.SerializeObject(copy);
            }
            internal IReadOnlyDictionary<string, object> ManagedRoots {
                get {
                    var roots = new Dictionary<string, object>(StringComparer.Ordinal) {
                        ["resident-loader/load"] = load,
                        ["resident-loader/info"] = info,
                        ["resident-loader/finished-transition"] = transition
                    };
                    foreach (var pair in slots.ManagedRoots) roots[pair.Key] = pair.Value;
                    return roots;
                }
            }
            internal readonly string[] CoverageGaps;
            internal readonly ResidentLoaderData Snapshot;
            internal int Ordinal => ordinal;
            internal string Source => owner.sourceName;
            internal string Target => load.TargetSceneName;
            internal ResidentLoaderBoundary(LoadingSerialSceneProbe owner)
            {
                this.owner = owner;
                load = owner.transition ?? throw Error("SceneLoad owner is absent");
                info = owner.checkpointLoadInfo ?? throw Error("load info is absent");
                begin = owner.residentBeginRoutine ?? throw Error("actual Begin iterator is absent");
                transition = owner.checkpointTransitionRoutine ?? throw Error("actual GameManager iterator is absent");
                ordinal = owner.ordinal; thread = Thread.CurrentThread.ManagedThreadId;
                stagingHandle = owner.staging.handle; wasObserved = owner.observedBoundary;
                slots = new ResidentProcessSlots("loader", owner, ResidentFieldOwnership, Require);
                var gaps = new SortedSet<string>(slots.Snapshot.CoverageGaps, StringComparer.Ordinal);
                // A completed native unload operation must have been released
                // before this point. Do not serialize its process-local handle.
                foreach (var field in CanonicalCarryGraphCodec.Fields(begin.GetType()))
                    if (typeof(UnityEngine.AsyncOperation).IsAssignableFrom(field.FieldType) && field.GetValue(begin) != null)
                        gaps.Add("loader-retains-native-async-operation:" + field.Name);
                if (info.GetType() != typeof(GameManager.SceneLoadInfo)) gaps.Add("loader-fetch-activation-protocol:" + info.GetType().FullName);
                var iteratorFields = CanonicalCarryGraphCodec.Fields(begin.GetType()).Select(field => {
                    var value = field.GetValue(begin); iteratorValues.Add(field, value);
                    string portable;
                    if (value == null) portable = "null";
                    else if (ReferenceEquals(value, owner)) portable = "execution-policy";
                    else if (ReferenceEquals(value, load)) portable = "resident-loader/load";
                    else if (value is OriginalBegin original && original.GetInvocationList().Length == 1) portable = "installed-original-BeginRoutine";
                    else if (field.Name == "<>l__initialThreadId" && field.FieldType == typeof(int) && (int)value == thread) portable = "execution-game-thread";
                    else if (field.FieldType == typeof(int)) portable = "int32:" + ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    else if (field.FieldType == typeof(bool)) portable = (bool)value ? "bool:1" : "bool:0";
                    else { portable = "unsupported"; gaps.Add("loader-parked-iterator-field:" + field.Name); }
                    return new ResidentIteratorField { Token = field.MetadataToken, Attributes = (int)field.Attributes, Name = field.Name, Value = portable,
                        DeclaringType = ResidentManagedTypeCatalog.DescribeType(field.DeclaringType!), ValueType = ResidentManagedTypeCatalog.DescribeType(field.FieldType) };
                }).ToArray();
                CoverageGaps = gaps.ToArray();
                Snapshot = new ResidentLoaderData { Owner = slots.Snapshot, Iterator = iteratorFields, CoverageGaps = (string[])CoverageGaps.Clone() };
                iteratorManifest = CanonicalIteratorJson(iteratorFields); RequireUnchanged();
            }
            internal void Require()
            {
                if (thread != Thread.CurrentThread.ManagedThreadId || !ReferenceEquals(instance, owner) || owner.failure != null
                    || !owner.awaitingBoundary || owner.observedBoundary != wasObserved || !owner.sourceUnloaded
                    || owner.source.isLoaded || !owner.staging.IsValid() || !owner.staging.isLoaded
                    || owner.staging.handle != stagingHandle || USceneManager.GetActiveScene().handle != stagingHandle
                    || !owner.checkpointTransitionFinished || owner.ordinal != ordinal
                    || !ReferenceEquals(owner.transition, load) || !ReferenceEquals(owner.checkpointLoadInfo, info)
                    || !ReferenceEquals(owner.residentBeginRoutine, begin) || !ReferenceEquals(owner.checkpointTransitionRoutine, transition))
                    throw Error("actual loader ownership or parked unloaded phase changed");
                var target = USceneManager.GetSceneByName(load.TargetSceneName);
                if (target.IsValid() && target.isLoaded) throw Error("target is already loaded");
                if (!owner.beginHook.IsApplied || !owner.beginHook.IsValid || owner.beginHook.Method.DeclaringType != typeof(SceneLoad)
                    || owner.beginHook.Method.Name != "BeginRoutine" || owner.beginHook.Target.DeclaringType != typeof(LoadingSerialSceneProbe)
                    || owner.beginHook.Target.Name != nameof(Begin) || !ReferenceEquals(owner.beginHook.DelegateTarget, owner))
                    throw Error("parked iterator no longer belongs to the installed original-loader hook");
                foreach (var field in iteratorValues) {
                    var current = field.Key.GetValue(begin);
                    if (field.Key.FieldType.IsValueType ? !Equals(field.Value, current) : !ReferenceEquals(field.Value, current))
                        throw Error("parked original-loader iterator changed: " + field.Key.Name);
                }
            }
            internal string? BindExternal(object value)
                => ReferenceEquals(value, owner) ? "resident-loader/execution/policy"
                : ReferenceEquals(value, begin) ? "resident-loader/execution/parked-begin" : slots.BindExternal(value);
            internal object? ResolveExternal(string key)
                => key == "resident-loader/execution/policy" ? owner
                : key == "resident-loader/execution/parked-begin" ? begin : slots.ResolveExternal(key);
            internal void RequireUnchanged() { Require(); slots.RequireUnchanged(); RequireManifest(); }
            private void RequireManifest() {
                if (iteratorManifest != CanonicalIteratorJson(Snapshot.Iterator) || !CoverageGaps.SequenceEqual(Snapshot.CoverageGaps))
                    throw Error("loader field contract was mutated");
            }
            internal void RequireComplete()
            { Require(); RequireManifest(); slots.RequireComplete(); if (CoverageGaps.Length != 0) throw Error(string.Join(";", CoverageGaps)); }
            internal void ReleaseAfterRestore()
            {
                Require();
                if (wasObserved) throw Error("restoration attempted a second Canon on the same loader boundary");
                owner.observedBoundary = true;
            }
            internal void RequireLayout(ResidentLoaderData source)
            {
                RequireComplete();
                if (source == null || source.FrameObservation || !string.IsNullOrEmpty(source.SceneDependencies)) throw Error("expected an original boundary loader restore packet");
                if (source.CoverageGaps == null || source.CoverageGaps.Length != 0
                    || CanonicalIteratorJson(source.Iterator) != iteratorManifest) {
                    var diff = "";
                    if (source.Iterator != null) {
                        var sJson = CanonicalIteratorJson(source.Iterator);
                        for (int i = 0; i < Math.Min(sJson.Length, iteratorManifest.Length); i++) {
                            if (sJson[i] != iteratorManifest[i]) {
                                var sSub = sJson.Substring(Math.Max(0, i - 20), Math.Min(40, sJson.Length - Math.Max(0, i - 20)));
                                var fSub = iteratorManifest.Substring(Math.Max(0, i - 20), Math.Min(40, iteratorManifest.Length - Math.Max(0, i - 20)));
                                diff += $" at {i}: src=[{sSub}] target=[{fSub}]";
                                break;
                            }
                        }
                    }
                    throw Error("source parked loader field/phase contract differs: " + diff);
                }
                slots.RequireLayout(source.Owner);
            }
            internal void Apply(ResidentMutationScope scope) { RequireComplete(); slots.Apply(scope); }
            internal void Verify() { RequireComplete(); slots.Verify(); }
            private static InvalidDataException Error(string message) => new InvalidDataException("Resident loader: " + message);
        }
    }
}
