using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Only the original SetEasingFunction table. The closed action remains a
    // reference to the same node in D; no callback target is constructed here.
    internal static class CanonicalEaseCallback
    {
        internal static readonly Type? OwnerType = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(assembly =>
            assembly.GetName().Name == "Assembly-CSharp")?.GetType("HutongGames.PlayMaker.Actions.EaseFsmAction", false);
        internal static readonly FieldInfo? Field = OwnerType?.GetField("ease", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        internal static Type? CallbackType => Field?.FieldType;
        private const string Module = "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f";
        private static readonly string[] Names = { "linear", "spring", "easeInQuad", "easeOutQuad", "easeInOutQuad", "easeInCubic",
            "easeOutCubic", "easeInOutCubic", "easeInQuart", "easeOutQuart", "easeInOutQuart", "easeInQuint", "easeOutQuint",
            "easeInOutQuint", "easeInSine", "easeOutSine", "easeInOutSine", "easeInExpo", "easeOutExpo", "easeInOutExpo",
            "easeInCirc", "easeOutCirc", "easeInOutCirc", "bounce", "easeInBack", "easeOutBack", "easeInOutBack", "elastic" };
        private static readonly int[] Tokens = { 0x06003c86, 0x06003c88, 0x06003c89, 0x06003c8a, 0x06003c8b, 0x06003c8c,
            0x06003c8d, 0x06003c8e, 0x06003c8f, 0x06003c90, 0x06003c91, 0x06003c92, 0x06003c93, 0x06003c94,
            0x06003c95, 0x06003c96, 0x06003c97, 0x06003c98, 0x06003c99, 0x06003c9a, 0x06003c9b, 0x06003c9c,
            0x06003c9d, 0x06003c9e, 0x06003c9f, 0x06003ca0, 0x06003ca1, 0x06003ca3 };
        internal static bool IsSlot(object owner, FieldInfo field) => OwnerType != null && Field != null && OwnerType.IsInstanceOfType(owner)
            && field.DeclaringType == OwnerType && field.Name == Field.Name && field.FieldType == CallbackType;
        internal static string Describe(Delegate callback, object owner)
        {
            if (callback.GetType() != CallbackType || callback.GetInvocationList().Length != 1 || !ReferenceEquals(callback.Target, owner))
                throw new InvalidDataException("Ease callback closed action or invocation differs.");
            var method = ResolveMethod(callback.Method.MetadataToken, callback.Method.Name);
            if (!Equals(callback.Method, method)) throw new InvalidDataException("Ease callback method identity differs.");
            var actualMvid = OwnerType!.Module.ModuleVersionId.ToString("D");
            if (actualMvid == Module && Array.IndexOf(Tokens, method.MetadataToken) >= 0)
                return Module + "/" + method.MetadataToken.ToString("x8");
            return actualMvid + "/" + method.Name;
        }
        private static MethodInfo ResolveMethod(int token, string? name = null)
        {
            if (OwnerType == null || OwnerType.Assembly.GetName().Name != "Assembly-CSharp")
                throw new InvalidDataException("Ease callback module or method is unregistered.");
            var actualMvid = OwnerType.Module.ModuleVersionId.ToString("D");
            if (actualMvid != Module)
                CanonicalSceneMappingCarry.WarnOnce("EaseCallback:Mvid:" + actualMvid,
                    "[HKTAS] EaseFsmAction ModuleVersionId differs from pinned original (continuing): actual=" + actualMvid);
            var index = Array.IndexOf(Tokens, token);
            if (index < 0 && !string.IsNullOrEmpty(name))
                index = Array.IndexOf(Names, name);
            if (index < 0)
                throw new InvalidDataException("Ease callback module or method is unregistered.");
            var method = OwnerType.GetMethod(Names[index], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(float), typeof(float), typeof(float) }, null);
            if (method == null || method.DeclaringType != OwnerType || method.IsStatic
                || method.IsGenericMethod || method.ReturnType != typeof(float))
                throw new InvalidDataException("Ease callback method layout differs.");
            if (method.MetadataToken != Tokens[index])
                CanonicalSceneMappingCarry.WarnOnce("EaseCallback:Token:" + Names[index] + ":" + method.MetadataToken,
                    "[HKTAS] EaseFsmAction." + Names[index] + " MetadataToken differs from pinned 0x" + Tokens[index].ToString("x8")
                    + " (continuing): actual=0x" + method.MetadataToken.ToString("x8"));
            return method;
        }
        internal static Delegate Bind(string identity, object owner)
        {
            if (OwnerType == null || CallbackType == null || !OwnerType.IsInstanceOfType(owner))
                throw new InvalidDataException("Ease callback target or identity differs.");
            var callback = Delegate.CreateDelegate(CallbackType, owner, ValidateIdentity(identity));
            var described = Describe(callback, owner);
            if (described != identity && ValidateIdentity(described) != callback.Method)
                throw new InvalidDataException("Ease callback binding changed.");
            return callback;
        }
        internal static MethodInfo ValidateIdentity(string identity)
        {
            if (identity == null) throw new InvalidDataException("Ease callback identity differs.");
            var slash = identity.IndexOf('/');
            if (slash != 36 || !Guid.TryParseExact(identity.Substring(0, slash), "D", out var identityMvid) || identityMvid == Guid.Empty)
                throw new InvalidDataException("Ease callback identity differs.");
            var suffix = identity.Substring(slash + 1);
            if (identity.Substring(0, slash) != Module)
                CanonicalSceneMappingCarry.WarnOnce("EaseCallback:IdentityMvid:" + identity.Substring(0, slash),
                    "[HKTAS] Ease callback identity MVID differs from pinned original (continuing): " + identity.Substring(0, slash));
            if (suffix.Length == 8 && int.TryParse(suffix, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var token))
                return ResolveMethod(token);
            if (Array.IndexOf(Names, suffix) >= 0)
                return ResolveMethod(-1, suffix);
            throw new InvalidDataException("Ease callback identity differs.");
        }
    }
}
