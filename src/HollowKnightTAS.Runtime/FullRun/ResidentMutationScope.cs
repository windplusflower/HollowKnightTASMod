using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Restore-only suppression. The original native object/registration code
    // still runs; managed game callbacks and constructors do not run against
    // an incomplete graph. Capture never enters this scope; callers use the
    // same admitted transaction for every common S0 entrance and restoration.
    internal sealed class ResidentMutationScope : IDisposable
    {
        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static ResidentMutationScope? activeScope, failedScope;
        private static readonly HashSet<string> Messages = new HashSet<string>(StringComparer.Ordinal) {
            "Awake", "Start", "OnEnable", "OnDisable", "OnDestroy", "Reset", "OnValidate", "Update", "FixedUpdate", "LateUpdate",
            "OnGUI", "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderObject", "OnWillRenderObject", "OnRenderImage",
            "OnBeforeSerialize", "OnAfterDeserialize", "OnLevelWasLoaded",
            "OnBecameVisible", "OnBecameInvisible", "OnTransformParentChanged", "OnTransformChildrenChanged", "OnBeforeTransformParentChanged",
            "OnRectTransformDimensionsChange", "OnCanvasHierarchyChanged", "OnCanvasGroupChanged", "OnDidApplyAnimationProperties",
            "OnAnimatorMove", "OnAnimatorIK", "OnAudioFilterRead", "OnApplicationPause", "OnApplicationFocus", "OnApplicationQuit",
            "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit", "OnCollisionEnter2D", "OnCollisionStay2D", "OnCollisionExit2D",
            "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit", "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D",
            "OnJointBreak", "OnJointBreak2D", "OnParticleCollision", "OnParticleTrigger", "OnParticleSystemStopped",
            "OnMouseDown", "OnMouseUp", "OnMouseUpAsButton", "OnMouseDrag", "OnMouseEnter", "OnMouseExit", "OnMouseOver"
        };
        private readonly HashSet<GameObject> objects = new HashSet<GameObject>(References<GameObject>.Instance);
        private readonly HashSet<GameObject> constructionOwners = new HashSet<GameObject>(References<GameObject>.Instance);
        private readonly HashSet<Component> components = new HashSet<Component>(References<Component>.Instance);
        private readonly HashSet<ScriptableObject> assets = new HashSet<ScriptableObject>(References<ScriptableObject>.Instance);
        private readonly HashSet<Type> types = new HashSet<Type>();
        private readonly HashSet<MethodBase> methods = new HashSet<MethodBase>();
        private readonly List<Hook> hooks = new List<Hook>();
        private readonly Stack<ConstructionWindow> construction = new Stack<ConstructionWindow>();
        private readonly Stack<ScriptableConstructionWindow> assetConstruction = new Stack<ScriptableConstructionWindow>();
        private readonly Action admission;
        private Dictionary<string,ResidentObjectNativeState.GlobalOwner>? nativeAssets;
        internal void BindNativeAssets(IEnumerable<ResidentObjectNativeState.GlobalOwner> owners)
        {
            Require();
            nativeAssets = owners.Where(x => x.Key.StartsWith(ResidentNativeAssetReference.Prefix,StringComparison.Ordinal))
                .ToDictionary(x => x.Key,StringComparer.Ordinal);
        }
        internal static IntPtr RequireNativeAsset(string key,int family)
        {
            var scope = activeScope ?? throw Error("native asset lookup outside the active transaction"); scope.Require();
            if (scope.nativeAssets == null || !scope.nativeAssets.TryGetValue(key,out var owner) || owner.Pointer == IntPtr.Zero)
                throw Error("native asset is absent from the independently observed target directory");
            var type = ResidentNativeAssetReference.NativeClass(owner.Pointer,family);
            var id = ResidentNativeMemory.I32(ResidentNativeMemory.Read(IntPtr.Add(owner.Pointer,8),4),0);
            if (ResidentLoadedAssetIndex.RegisteredPointer(id,out var check) != owner.Pointer) throw Error("native asset ID has another owner");
            var origin = new LoadingAssetProvenanceObservation().Capture(id);
            if (origin.kind != "serialized" || ResidentNativeAssetDirectory.ProvenanceKey(type,origin.sourcePath,origin.localIdentifier) != key)
                throw Error("target native asset provenance differs");
            check(); scope.Require(); return owner.Pointer;
        }
        private readonly int thread;
        internal ResidentExecutionOwnership? ExecutionOwnership { get; }
        private State state;
        private Exception? failure;
        private enum State { Prepared, Active, Completed, Faulted, Disposed }

        internal ResidentMutationScope(IEnumerable<GameObject> ownedObjects, Action admitted)
            : this(ownedObjects, admitted, null) { }
        internal ResidentMutationScope(ResidentObjectDirectory directory, Action admitted)
            : this((directory ?? throw new ArgumentNullException(nameof(directory))).NativeBindings.Values.OfType<GameObject>(), admitted, directory.Ownership) { }
        internal ResidentMutationScope(IEnumerable<GameObject> ownedObjects, Action admitted, ResidentExecutionOwnership? ownership)
        {
            admission = admitted ?? throw new ArgumentNullException(nameof(admitted));
            ExecutionOwnership = ownership;
            thread = Thread.CurrentThread.ManagedThreadId;
            if (failedScope != null) throw Error("a failed restore requires process retirement");
            if (ownedObjects == null) throw new ArgumentNullException(nameof(ownedObjects));
            try { foreach (var owner in ownedObjects) Track(owner); }
            catch { RetireHooks(); throw; }
        }
        internal ResidentMutationScope(IEnumerable<MonoBehaviour> ownedComponents, Action admitted)
            : this((ownedComponents ?? throw new ArgumentNullException(nameof(ownedComponents))).Select(owner => owner.gameObject).Distinct(), admitted) { }

        internal void Enter()
        {
            if (state != State.Prepared || activeScope != null || failedScope != null) throw Error("restore scope is reused or nested");
            try { RequireThread(); admission(); state = State.Active; activeScope = this; }
            catch (Exception error) { Fault(error); throw; }
        }
        internal void Require()
        {
            if (state != State.Active || !ReferenceEquals(activeScope, this) || failure != null || failedScope != null)
                throw Error("restore mutation is outside its active transaction", failure);
            try { RequireThread(); admission(); }
            catch (Exception error) { Fault(error); throw; }
        }
        internal bool Owns(Component owner)
        {
            if (ReferenceEquals(owner, null) || (ExecutionOwnership?.IsExecution(owner) ?? false)) return false;
            if (components.Contains(owner)) return true;
            return OwnsComponentGameObject(owner);
        }
        // Keep the native fallback outside the pure managed short-circuit path.
        // This also makes identity-only ownership checks usable before a newly
        // allocated wrapper has a native gameObject during construction.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool OwnsComponentGameObject(Component owner) => owner != null && Owns(owner.gameObject);
        internal bool Owns(GameObject owner) => !ReferenceEquals(owner, null) && !(ExecutionOwnership?.IsProtected(owner) ?? false) && objects.Contains(owner);
        internal bool Owns(ScriptableObject owner) => !ReferenceEquals(owner, null) && assets.Contains(owner);
        internal void Track(GameObject owner)
        {
            RequireTrack();
            if (owner == null) throw Error("cannot track an absent native GameObject");
            constructionOwners.Add(owner);
            if (!(ExecutionOwnership?.IsProtected(owner) ?? false)) objects.Add(owner);
            foreach (var component in owner.GetComponents<Component>()) Track(component);
        }
        internal void Track(Component owner)
        {
            RequireTrack();
            if (owner == null) throw Error("cannot track an absent native component");
            if (ExecutionOwnership?.IsExecution(owner) ?? false) return;
            var gameObject = owner.gameObject; constructionOwners.Add(gameObject);
            if (!(ExecutionOwnership?.IsProtected(gameObject) ?? false)) objects.Add(gameObject);
            components.Add(owner);
            if (owner is MonoBehaviour) PrepareType(owner.GetType());
        }
        internal void Track(ScriptableObject owner)
        {
            RequireTrack();
            if (!ResidentObjectDirectory.IsLive(owner)) throw Error("cannot track an absent native ScriptableObject");
            assets.Add(owner); PrepareType(owner.GetType());
        }
        internal void Mutate(Action mutation)
        {
            if (mutation == null) throw new ArgumentNullException(nameof(mutation));
            Require();
            try { mutation(); Require(); }
            catch (Exception error) { Fault(error); throw; }
        }
        internal IDisposable Construction(GameObject owner, Type actualType)
        {
            Require();
            if (owner == null || !constructionOwners.Contains(owner) || actualType == null || !typeof(Component).IsAssignableFrom(actualType)
                || actualType.IsAbstract || actualType.ContainsGenericParameters) throw Error("component construction is outside the owned native directory");
            try {
                var dependencies = RequiredTypes(actualType);
                foreach (var type in dependencies.Where(type => typeof(MonoBehaviour).IsAssignableFrom(type))) PrepareType(type);
                var window = new ConstructionWindow(this, owner, dependencies); construction.Push(window); return window;
            } catch (Exception error) { Fault(error); throw; }
        }
        internal IDisposable ScriptableConstruction(Type actualType)
        {
            Require();
            if (actualType == null || !typeof(ScriptableObject).IsAssignableFrom(actualType) || actualType.IsAbstract || actualType.ContainsGenericParameters)
                throw Error("asset construction requires one exact concrete ScriptableObject type");
            try {
                PrepareType(actualType);
                var window = new ScriptableConstructionWindow(this, actualType); assetConstruction.Push(window); return window;
            } catch (Exception error) { Fault(error); throw; }
        }
        internal void Complete(Action verifyCompleteState)
        {
            Require();
            if (verifyCompleteState == null || construction.Count != 0 || assetConstruction.Count != 0) throw Error("restore completion lacks a closed graph/native verification");
            try {
                verifyCompleteState(); Require(); state = State.Completed; activeScope = null; RetireHooks();
            } catch (Exception error) { Fault(error); throw; }
        }
        internal void Fault(Exception error)
        {
            failure ??= error ?? Error("restore failed without an exception");
            state = State.Faulted; failedScope = this;
            // Keep these hooks and the owned object directory rooted. No game
            // callback may consume a partial graph after a failed transaction.
            activeScope = this;
        }
        private void RequireTrack()
        {
            RequireThread();
            if (state != State.Prepared) Require();
        }
        private void RequireThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread) throw Error("restore moved away from its admitted Unity thread");
        }
        private bool Suppress(object owner, bool constructor)
        {
            // This wins even over a same-type construction window and even
            // when a stale broad object set contains a protected compound GO.
            // Its current execution driver must keep running this transaction.
            if (owner is Component execution && (ExecutionOwnership?.IsExecution(execution) ?? false)) return false;
            if (state != State.Active && state != State.Faulted) return false;
            if (Thread.CurrentThread.ManagedThreadId != thread) {
                if ((owner is Component foreignThreadOwner && components.Contains(foreignThreadOwner))
                    || (owner is ScriptableObject foreignThreadAsset && assets.Contains(foreignThreadAsset))) {
                    Fault(Error("owned restored callback entered from another thread")); return true;
                }
                return false;
            }
            if (owner is ScriptableObject asset) {
                // Native creation can invoke Awake/OnEnable immediately after
                // constructing the managed wrapper, before CreateInstance
                // returns. Bind that exact wrapper inside its one type window.
                if (assetConstruction.Count != 0 && assetConstruction.Peek().Type == owner.GetType()) {
                    assetConstruction.Peek().Observe(asset); assets.Add(asset);
                }
                if (!Owns(asset)) return false;
                if (state == State.Active) Require();
                return true;
            }
            if (constructor && construction.Count != 0 && construction.Peek().Types.Contains(owner.GetType())) return true;
            if (owner is not Component component || !Owns(component)) return false;
            if (state == State.Active) Require();
            return true;
        }
        private void PrepareType(Type type)
        {
            var nativeBase = typeof(MonoBehaviour).IsAssignableFrom(type) ? typeof(MonoBehaviour)
                : typeof(ScriptableObject).IsAssignableFrom(type) ? typeof(ScriptableObject) : null;
            if (nativeBase == null || type.IsAbstract || type.ContainsGenericParameters) return;
            if (!types.Add(type)) return;
            for (var current = type; current != null && current != nativeBase; current = current.BaseType) {
                foreach (var method in current.GetMethods(Declared).Where(method => Messages.Contains(method.Name) && !method.IsStatic && !method.IsAbstract))
                    Install(method, false);
                foreach (var constructor in current.GetConstructors(Declared)) Install(constructor, true);
            }
        }
        private void Install(MethodBase method, bool constructor)
        {
            if (!methods.Add(method)) return;
            if (method.ContainsGenericParameters || method.GetMethodBody() == null) throw Error("actual managed callback has no supported method body: " + method);
            var ownerType = method.DeclaringType ?? throw Error("actual callback declaring type is absent");
            var arguments = new[] { ownerType }.Concat(method.GetParameters().Select(parameter => parameter.ParameterType)).ToArray();
            var returns = method is MethodInfo info ? info.ReturnType : typeof(void);
            if (returns.IsByRef || arguments.Length > 14) throw Error("actual lifecycle signature exceeds the generic detour ABI: " + method);
            var originalType = Expression.GetDelegateType(arguments.Concat(new[] { returns }).ToArray());
            var hookType = Expression.GetDelegateType(new[] { originalType }.Concat(arguments).Concat(new[] { returns }).ToArray());
            var parameters = new[] { Expression.Parameter(originalType, "original") }.Concat(arguments.Select((argument, index) => Expression.Parameter(argument, "argument" + index))).ToArray();
            var suppress = Expression.Call(Expression.Constant(this), typeof(ResidentMutationScope).GetMethod(nameof(Suppress), BindingFlags.Instance | BindingFlags.NonPublic)!,
                Expression.Convert(parameters[1], typeof(object)), Expression.Constant(constructor));
            var skipped = new List<Expression>();
            var declaredParameters = method.GetParameters();
            for (var index = 0; index < declaredParameters.Length; index++) if (declaredParameters[index].IsOut)
                skipped.Add(Expression.Assign(parameters[index + 2], Expression.Default(declaredParameters[index].ParameterType.GetElementType()!)));
            skipped.Add(Expression.Default(returns));
            var body = Expression.Condition(suppress, Expression.Block(skipped), Expression.Invoke(parameters[0], parameters.Skip(1)));
            hooks.Add(new Hook(method, Expression.Lambda(hookType, body, parameters).Compile()));
        }
        private static HashSet<Type> RequiredTypes(Type requested)
        {
            var result = new HashSet<Type>(); var pending = new Stack<Type>(); pending.Push(requested);
            while (pending.Count != 0) {
                var current = pending.Pop(); if (!result.Add(current)) continue;
                for (var type = current; type != null && typeof(Component).IsAssignableFrom(type); type = type.BaseType)
                    foreach (var attribute in CustomAttributeData.GetCustomAttributes(type).Where(attribute => attribute.AttributeType == typeof(RequireComponent))) {
                        foreach (var argument in attribute.ConstructorArguments)
                            if (argument.ArgumentType == typeof(Type) && argument.Value is Type dependency) pending.Push(dependency);
                        foreach (var argument in attribute.NamedArguments)
                            if (argument.TypedValue.ArgumentType == typeof(Type) && argument.TypedValue.Value is Type dependency) pending.Push(dependency);
                    }
                if (result.Count > 100000) throw Error("component dependency directory exceeds the resident graph bound");
            }
            return result;
        }
        private void RetireHooks() { for (var index = hooks.Count - 1; index >= 0; index--) hooks[index].Dispose(); hooks.Clear(); }
        public void Dispose()
        {
            if (state == State.Disposed) return;
            if (state == State.Active || state == State.Faulted) { Fault(Error("restore scope was abandoned before complete verification")); return; }
            RetireHooks(); state = State.Disposed;
        }
        private sealed class ConstructionWindow : IDisposable
        {
            private readonly ResidentMutationScope owner;
            internal readonly GameObject GameObject;
            internal readonly HashSet<Type> Types;
            private bool disposed;
            internal ConstructionWindow(ResidentMutationScope owner, GameObject gameObject, HashSet<Type> types) { this.owner = owner; GameObject = gameObject; Types = types; }
            public void Dispose()
            {
                if (disposed) return;
                if (owner.construction.Count == 0 || !ReferenceEquals(owner.construction.Peek(), this)) {
                    var error = Error("component construction windows are unbalanced"); owner.Fault(error); throw error;
                }
                owner.construction.Pop(); disposed = true;
                if (GameObject != null) foreach (var component in GameObject.GetComponents<Component>()) owner.Track(component);
            }
        }
        private sealed class ScriptableConstructionWindow : IDisposable
        {
            private readonly ResidentMutationScope owner;
            internal readonly Type Type;
            private ScriptableObject? instance;
            private bool disposed;
            internal ScriptableConstructionWindow(ResidentMutationScope owner, Type type) { this.owner = owner; Type = type; }
            internal void Observe(ScriptableObject value)
            {
                if (!ReferenceEquals(instance, null) && !ReferenceEquals(instance, value)) {
                    var error = Error("ScriptableObject construction produced another wrapper in the same window"); owner.Fault(error); throw error;
                }
                instance = value;
            }
            public void Dispose()
            {
                if (disposed) return;
                if (owner.assetConstruction.Count == 0 || !ReferenceEquals(owner.assetConstruction.Peek(), this)) {
                    var error = Error("ScriptableObject construction windows are unbalanced"); owner.Fault(error); throw error;
                }
                owner.assetConstruction.Pop(); disposed = true;
            }
        }
        private static InvalidOperationException Error(string message, Exception? inner = null) => new InvalidOperationException("Resident mutation: " + message, inner);
        private sealed class References<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly References<T> Instance = new References<T>();
            public bool Equals(T? left, T? right) => ReferenceEquals(left, right);
            public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
