using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Original getter depends on both this managed D state and Unity's imported
    // native realtime clock. Never call FsmTime.RealtimeSinceStartup to observe
    // or verify it: the original getter can change the offset on either branch.
    internal static class CanonicalFsmTimeCarry
    {
        private const string OwnerName = "HutongGames.PlayMaker.FsmTime";
        private const string OriginalModule = "29eb59fa-5c99-43cd-8f33-6db074533d31";
        internal sealed class Data
        {
            public int Schema { get; set; } = 1;
            public string ModuleVersionId { get; set; } = "";
            public bool FirstUpdateHasHappened { get; set; }
            public uint TotalEditorPlayerPausedTimeBits { get; set; }
            public uint RealtimeLastUpdateBits { get; set; }
            public int FrameCountLastUpdate { get; set; }
            public bool RestoreBindingQualified => false;
            public string Dependency => "Original getter clock dependency: apply only after native clock Import, at the parked boundary";
        }
        private sealed class Fields
        {
            internal readonly FieldInfo First, Offset, LastRealtime, LastFrame;
            internal Fields(Type owner)
            {
                if (owner.FullName != OwnerName || owner.Assembly.GetName().Name != "PlayMaker"
                    || owner.Assembly.IsDynamic || !owner.IsAbstract || !owner.IsSealed
                    || owner.Module.ModuleVersionId.ToString("D") != OriginalModule)
                    throw new InvalidDataException("Original FsmTime owner/module binding differs.");
                var all = owner.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                var names = new[] { "firstUpdateHasHappened", "frameCountLastUpdate", "realtimeLastUpdate", "totalEditorPlayerPausedTime" };
                if (all.Length != 4 || !all.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(names)
                    || all.Any(x => !x.IsStatic || !x.IsPrivate || x.IsInitOnly || x.IsLiteral || x.IsDefined(typeof(ThreadStaticAttribute), false)))
                    throw new InvalidDataException("Original FsmTime four-field static layout differs.");
                FieldInfo Bind(string name, Type type)
                {
                    var field = all.Single(x => x.Name == name);
                    if (field.FieldType != type) throw new InvalidDataException("Original FsmTime field type differs: " + name);
                    return field;
                }
                First = Bind("firstUpdateHasHappened", typeof(bool)); Offset = Bind("totalEditorPlayerPausedTime", typeof(float));
                LastRealtime = Bind("realtimeLastUpdate", typeof(float)); LastFrame = Bind("frameCountLastUpdate", typeof(int));
            }
            internal Data Capture()
            {
                var value = new Data { ModuleVersionId = OriginalModule, FirstUpdateHasHappened = (bool)First.GetValue(null)!,
                    TotalEditorPlayerPausedTimeBits = Bits((float)Offset.GetValue(null)!), RealtimeLastUpdateBits = Bits((float)LastRealtime.GetValue(null)!),
                    FrameCountLastUpdate = (int)LastFrame.GetValue(null)! };
                Validate(value); return value;
            }
            internal void Write(Data value)
            {
                // No setter, getter, Update or FSM event. The outer transaction
                // restores these same four raw fields on any partial failure.
                First.SetValue(null, value.FirstUpdateHasHappened);
                Offset.SetValue(null, Float(value.TotalEditorPlayerPausedTimeBits));
                LastRealtime.SetValue(null, Float(value.RealtimeLastUpdateBits));
                LastFrame.SetValue(null, value.FrameCountLastUpdate);
            }
        }
        private static Fields Bind()
        {
            var owners = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.GetName().Name == "PlayMaker")
                .Select(a => a.GetType(OwnerName, false)).Where(t => t != null).ToArray();
            if (owners.Length != 1) throw new InvalidDataException("Original initialized FsmTime owner is absent or ambiguous.");
            // Only the already-used original FsmTime type is read. No arbitrary
            // type scan or RunClassConstructor is performed here.
            return new Fields(owners[0]!);
        }
        internal static Data Capture() => Bind().Capture();
        internal static Prepared PrepareApply(Data source, Func<bool> nativeClockImportedAndParked)
        {
            var fields = Bind();
            return PrepareApply(source, fields.Capture, fields.Write, nativeClockImportedAndParked);
        }
        // Pure bounded transaction seam: tests inject raw four-field access,
        // while the runtime overload above always binds the pinned real owner.
        internal static Prepared PrepareApply(Data source, Func<Data> captureRawFields, Action<Data> writeRawFields,
            Func<bool> nativeClockImportedAndParked)
        {
            if (captureRawFields == null || writeRawFields == null || nativeClockImportedAndParked == null) throw new ArgumentNullException();
            var frozen = Copy(source); var old = Copy(captureRawFields());
            if (frozen.ModuleVersionId != old.ModuleVersionId) throw new InvalidDataException("FsmTime source/target module differs.");
            return new Prepared(frozen, old, captureRawFields, writeRawFields, nativeClockImportedAndParked);
        }
        internal sealed class Prepared
        {
            private readonly Data source, old;
            private readonly Func<Data> capture; private readonly Action<Data> write; private readonly Func<bool> ready;
            private int consumed;
            internal Prepared(Data source, Data old, Func<Data> capture, Action<Data> write, Func<bool> ready)
            { this.source = source; this.old = old; this.capture = capture; this.write = write; this.ready = ready; }
            public bool ApplicationExecuted { get; private set; }
            public bool ReadBackMatches { get; private set; }
            public bool Faulted { get; private set; }
            public bool RestoreBindingQualified => false;
            internal void Apply()
            {
                if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0) throw new InvalidOperationException("FsmTime application is single-use.");
                try
                {
                    if (!ready()) throw new InvalidOperationException("FsmTime application requires imported native clock and parked world.");
                    if (!Equal(old, capture())) throw new InvalidDataException("FsmTime target changed after preparation.");
                    ApplicationExecuted = true;
                    try
                    {
                        write(Copy(source));
                        if (!ready()) throw new InvalidOperationException("FsmTime native clock/boundary guard changed during application.");
                        if (!Equal(source, capture())) throw new InvalidDataException("FsmTime source bit readback differs.");
                        ReadBackMatches = true;
                    }
                    catch (Exception applicationError)
                    {
                        try
                        {
                            write(Copy(old));
                            if (!Equal(old, capture())) throw new InvalidDataException("FsmTime rollback bit readback differs.");
                        }
                        catch (Exception rollbackError)
                        { throw new AggregateException("FsmTime application and rollback failed; transaction permanently faulted.", applicationError, rollbackError); }
                        throw;
                    }
                }
                catch { Faulted = true; throw; }
            }
        }
        internal static uint ComputeRealtimeBits(Data state, float unityRealtime)
        {
            Validate(state); RequireFinite(unityRealtime, "original Unity realtime");
            if (!state.FirstUpdateHasHappened) return 0;
            var offset = Float(state.TotalEditorPlayerPausedTimeBits);
            // Original bge.un keeps offset when realtime >= offset, otherwise
            // sets offset to +0. This computes its result without that write.
            if (unityRealtime < offset) offset = 0;
            return Bits(unityRealtime - offset);
        }
        internal static void Validate(Data value)
        {
            if (value == null || value.Schema != 1 || value.ModuleVersionId != OriginalModule)
                throw new InvalidDataException("FsmTime source schema/original module differs.");
            RequireFinite(Float(value.TotalEditorPlayerPausedTimeBits), "paused-time offset");
            RequireFinite(Float(value.RealtimeLastUpdateBits), "last realtime");
        }
        private static void RequireFinite(float value, string label)
        { if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("FsmTime " + label + " is nonfinite."); }
        private static Data Copy(Data value)
        {
            Validate(value);
            return new Data { Schema = value.Schema, ModuleVersionId = value.ModuleVersionId, FirstUpdateHasHappened = value.FirstUpdateHasHappened,
                TotalEditorPlayerPausedTimeBits = value.TotalEditorPlayerPausedTimeBits, RealtimeLastUpdateBits = value.RealtimeLastUpdateBits,
                FrameCountLastUpdate = value.FrameCountLastUpdate };
        }
        internal static bool Equal(Data left, Data right)
        {
            Validate(left); Validate(right);
            return left.ModuleVersionId == right.ModuleVersionId && left.FirstUpdateHasHappened == right.FirstUpdateHasHappened
                && left.TotalEditorPlayerPausedTimeBits == right.TotalEditorPlayerPausedTimeBits && left.RealtimeLastUpdateBits == right.RealtimeLastUpdateBits
                && left.FrameCountLastUpdate == right.FrameCountLastUpdate;
        }
        internal static uint Bits(float value) => BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
        private static float Float(uint value) => BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
    }
}
