using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Registered copy-only queries for native value wrappers. Never use the
    // TextGenerator.verts/characters/lines properties: they mutate its caches.
    internal sealed class LoadingNativeValueObservation : IDisposable
    {
        private readonly LoadingFieldDigest values;
        private readonly Dictionary<Tuple<Type, IntPtr>, string> identities = new Dictionary<Tuple<Type, IntPtr>, string>();
        private readonly Dictionary<IntPtr, string> steamTables = new Dictionary<IntPtr, string>();
        private LoadingMaterialValueObservation? materialValues;
        internal LoadingNativeValueObservation(Func<object, string?> reference)
        {
            values = new LoadingFieldDigest(reference, type => type == typeof(UnityEngine.Object),
                packedArrayElement: LoadingPackedUnityArrays.Enabled ? LoadingPackedUnityArrays.Registered : (Func<Type, bool>?)null);
        }

        internal void BeginSample() { identities.Clear(); steamTables.Clear(); materialValues?.BeginSample(); }

        internal string? Field(object owner, FieldInfo field, object? value)
        {
            // Most fields are scalar managed values. Do not reflect on their
            // owner type for the three registered Steam pointer field names.
            if (field.Name == "m_CCallbackBase" || field.Name == "m_pVTable" || field.Name == "m_vfptr")
            {
                var steam = SteamField(owner, field, value);
                if (steam != null) return steam;
            }
            if (field.Name != "m_Ptr" || !(value is IntPtr pointer)) return null;
            // The pinned native reader enumerates nonempty blocks. It rejects
            // resource kinds whose bindings have not yet been registered.
            var emptyBlock = owner is MaterialPropertyBlock block && pointer != IntPtr.Zero && block.isEmpty;
            if (!(owner is AnimationCurve) && !(owner is RectOffset) && !(owner is TextGenerator)
                && !(owner is GUIStyle) && !(owner is GUIStyleState) && !(owner is MaterialPropertyBlock)) return null;
            if (pointer == IntPtr.Zero) return "native-value:null";
            var key = Tuple.Create(owner.GetType(), pointer);
            if (identities.TryGetValue(key, out var existing)) return existing;
            object snapshot;
            if (emptyBlock)
                snapshot = new { isEmpty = true };
            else if (owner is MaterialPropertyBlock material)
            {
                materialValues ??= new LoadingMaterialValueObservation();
                var captured = materialValues.Capture(material, pointer);
                if (captured == null) return null;
                snapshot = captured;
            }
            else if (owner is AnimationCurve curve)
                snapshot = new { curve.preWrapMode, curve.postWrapMode, keys = curve.keys };
            else if (owner is RectOffset rectangle)
                snapshot = new { rectangle.left, rectangle.right, rectangle.top, rectangle.bottom };
            else if (owner is GUIStyle style)
                snapshot = Style(style);
            else if (owner is GUIStyleState state)
                snapshot = StyleState(state);
            else
            {
                var text = (TextGenerator)owner;
                if (text.vertexCount > 65536 || text.characterCount > 16384 || text.lineCount > 16384)
                    throw new InvalidOperationException("P0 text generation cache exceeds its observation budget.");
                snapshot = new { text.vertexCount, text.characterCount, text.lineCount,
                    vertices = text.GetVerticesArray(), characters = text.GetCharactersArray(), lines = text.GetLinesArray() };
            }
            var digest = values.Capture(snapshot, "native-value", expandRoot: true);
            if (digest.Unsupported.Count != 0)
                throw new InvalidOperationException("P0 native value query returned unsupported fields.");
            var id = "native-value:" + owner.GetType().FullName + ":" + identities.Count + ":" + digest.Sha256;
            identities.Add(key, id);
            return id;
        }

        private string? SteamField(object owner, FieldInfo field, object? value)
        {
            var type = owner.GetType();
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "Steamworks.Callback`1"
                && field.Name == "m_CCallbackBase")
            {
                // Validate the exact callback-owned allocation before describing
                // its relocatable bindings. Never invoke/register a callback.
                var disposed = type.GetField("m_bDisposed", instance);
                var tableField = type.GetField("m_CallbackBaseVTable", instance);
                var pointerField = type.GetField("m_pVTable", instance);
                if (disposed == null || tableField == null || pointerField == null
                    || (bool)disposed.GetValue(owner)! || value == null) return null;
                var pointer = (IntPtr)pointerField.GetValue(owner)!;
                var table = tableField.GetValue(owner);
                var basePointer = value.GetType().GetField("m_vfptr", instance);
                if (pointer == IntPtr.Zero || table == null || basePointer == null
                    || (IntPtr)basePointer.GetValue(value)! != pointer) return null;
                var members = table.GetType().GetFields(instance);
                if (members.Length != 3) return null;
                foreach (var member in members)
                {
                    if (!(member.GetValue(table) is Delegate callback)) return null;
                    var offset = checked((int)Marshal.OffsetOf(table.GetType(), member.Name));
                    if (offset < 0 || offset >= 3 * IntPtr.Size
                        || Marshal.ReadIntPtr(pointer, offset) != Marshal.GetFunctionPointerForDelegate(callback))
                        return null;
                }
                if (!steamTables.ContainsKey(pointer))
                    steamTables.Add(pointer, "steam-callback-vtable:" + type.FullName + ":" + steamTables.Count);
            }
            if ((field.Name == "m_pVTable" || (type.FullName == "Steamworks.CCallbackBase" && field.Name == "m_vfptr"))
                && value is IntPtr address && steamTables.TryGetValue(address, out var identity)) return identity;
            return null;
        }

        private static object StyleState(GUIStyleState state)
            => new { state.background, state.textColor };

        private static object Style(GUIStyle style)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            // Public state/rectangle/name getters populate the source's managed
            // caches. Borrow native subobjects using the engine's non-owning
            // wrappers instead; their finalizers cannot free the source style.
            var getState = typeof(GUIStyle).GetMethod("GetStyleStatePtr", instance)
                ?? throw new InvalidOperationException("GUIStyle state query unavailable.");
            var wrapState = typeof(GUIStyleState).GetMethod("GetGUIStyleState", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("GUIStyleState borrowed wrapper unavailable.");
            var getRectangle = typeof(GUIStyle).GetMethod("GetRectOffsetPtr", instance)
                ?? throw new InvalidOperationException("GUIStyle rectangle query unavailable.");
            var wrapRectangle = typeof(RectOffset).GetConstructor(instance, null, new[] { typeof(object), typeof(IntPtr) }, null)
                ?? throw new InvalidOperationException("RectOffset borrowed wrapper unavailable.");
            var states = new object[8];
            for (var i = 0; i < states.Length; i++)
            {
                var pointer = getState.Invoke(style, new object[] { i });
                var state = (GUIStyleState)wrapState.Invoke(null, new[] { style, pointer })!;
                states[i] = StyleState(state);
            }
            var rectangles = new object[4];
            for (var i = 0; i < rectangles.Length; i++)
            {
                var pointer = getRectangle.Invoke(style, new object[] { i });
                var rectangle = (RectOffset)wrapRectangle.Invoke(new[] { style, pointer });
                rectangles[i] = new { rectangle.left, rectangle.right, rectangle.top, rectangle.bottom };
            }
            var rawName = typeof(GUIStyle).GetProperty("rawName", instance)
                ?? throw new InvalidOperationException("GUIStyle raw name query unavailable.");
            var clipOffset = typeof(GUIStyle).GetProperty("Internal_clipOffset", instance)
                ?? throw new InvalidOperationException("GUIStyle clip offset query unavailable.");
            return new { name = (string)rawName.GetValue(style, null), style.font, style.imagePosition,
                style.alignment, style.wordWrap, style.clipping, style.contentOffset,
                clipOffset = (Vector2)clipOffset.GetValue(style, null), style.fixedWidth, style.fixedHeight,
                style.stretchWidth, style.stretchHeight, style.fontSize, style.fontStyle, style.richText,
                states, rectangles };
        }

        public void Dispose() => values.Dispose();
    }
}
