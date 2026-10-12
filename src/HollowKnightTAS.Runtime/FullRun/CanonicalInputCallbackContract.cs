using System;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Subscriptions are supplied by original input setup, never created or
    // invoked by restore. D carries their complete ordered method/target
    // descriptors so a different callback chain cannot pass a type check.
    internal static class CanonicalInputCallbackContract
    {
        internal const string Root = "R6/InputManager-callbacks";
        internal static readonly string[] Slots = { "OnSetup", "OnUpdate", "OnReset", "OnDeviceAttached",
            "OnDeviceDetached", "OnActiveDeviceChanged", "OnUpdateDevices", "OnCommitDevices" };
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(Slot), typeof(Slot[]), typeof(Method), typeof(Method[]) };
        internal sealed class Data { public Slot[] Callbacks = Array.Empty<Slot>(); }
        internal sealed class Slot { public string Field = ""; public Method[] Methods = Array.Empty<Method>(); }
        internal sealed class Method
        {
            public string Assembly = "", ModuleMvid = "", DeclaringType = "", Name = "", TargetBinding = "", TargetType = "";
            public int Token;
            public bool IsStatic;
        }
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var owner = view.Root(Root);
            var slots = view.Field(owner, typeof(Data), nameof(Data.Callbacks)) as Slot[]
                ?? throw Invalid("missing callback array");
            // Slot/Method objects are detached graph nodes, filled by the
            // codec before preflight; they are never existing game owners.
            return Copy(slots);
        }
        internal static Data Copy(Slot[] slots)
        {
            var data = new Data { Callbacks = slots }; Validate(data);
            return new Data { Callbacks = slots.Select(slot => new Slot { Field = slot.Field, Methods = slot.Methods.Select(method =>
                new Method { Assembly = method.Assembly, ModuleMvid = method.ModuleMvid, DeclaringType = method.DeclaringType,
                    Name = method.Name, TargetBinding = method.TargetBinding, TargetType = method.TargetType,
                    Token = method.Token, IsStatic = method.IsStatic }).ToArray() }).ToArray() };
        }
        internal static void RequireSame(Data source, Data prepared)
        {
            Validate(source); Validate(prepared);
            for (var slot = 0; slot < Slots.Length; slot++) {
                var expected = source.Callbacks[slot].Methods; var actual = prepared.Callbacks[slot].Methods;
                if (expected.Length != actual.Length) throw Invalid("invocation count " + Slots[slot]);
                for (var index = 0; index < expected.Length; index++) {
                    var first = expected[index]; var second = actual[index];
                    if (first.Assembly != second.Assembly || first.ModuleMvid != second.ModuleMvid || first.Token != second.Token
                        || first.DeclaringType != second.DeclaringType || first.Name != second.Name || first.IsStatic != second.IsStatic
                        || first.TargetBinding != second.TargetBinding || first.TargetType != second.TargetType)
                        throw Invalid("ordered method/target " + Slots[slot] + "/" + index);
                }
            }
        }
        private static void Validate(Data data)
        {
            if (data == null || data.Callbacks == null || data.Callbacks.Length != Slots.Length)
                throw Invalid("slot count");
            for (var slot = 0; slot < Slots.Length; slot++) {
                var current = data.Callbacks[slot];
                if (current == null || current.Field != Slots[slot] || current.Methods == null || current.Methods.Length > 256)
                    throw Invalid("slot identity/bound");
                foreach (var method in current.Methods) {
                    if (method == null || method.Token <= 0 || !Guid.TryParseExact(method.ModuleMvid, "D", out var mvid)
                        || mvid.ToString("D") != method.ModuleMvid) throw Invalid("method identity");
                    foreach (var text in new[] { method.Assembly, method.DeclaringType, method.Name, method.TargetBinding, method.TargetType })
                        if (text == null || text.Length > 16384 || text.IndexOf('\0') >= 0) throw Invalid("method descriptor text");
                    if (method.Assembly.Length == 0 || method.DeclaringType.Length == 0 || method.Name.Length == 0
                        || (method.TargetBinding.Length == 0) != (method.TargetType.Length == 0)) throw Invalid("method/target descriptor");
                }
            }
        }
        private static InvalidDataException Invalid(string detail) => new InvalidDataException("Canonical input callback binding differs: " + detail);
    }
}
