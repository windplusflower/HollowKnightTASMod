using System;
using System.IO;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalBossChallengeCallback
    {
        internal static bool IsType(Type type) => type == typeof(Action) || type == typeof(BossSceneController.SetupEventDelegate);
        internal static Type Target => typeof(CanonicalBossChallengeContext);
        private static MethodInfo Method(string kind) => Target.GetMethod(kind == "setup" ? "Setup" : kind == "complete" ? "Complete"
            : throw new InvalidDataException("Unknown challenge callback identity."), BindingFlags.Instance | BindingFlags.NonPublic,
            null, kind == "setup" ? new[] { typeof(BossSceneController) } : Type.EmptyTypes, null)!;
        internal static string Describe(Delegate callback)
        {
            var kind = callback.GetType() == typeof(BossSceneController.SetupEventDelegate) ? "setup"
                : callback.GetType() == typeof(Action) ? "complete" : throw new InvalidDataException("Challenge callback type differs.");
            if (callback.GetInvocationList().Length != 1 || callback.Target?.GetType() != Target || callback.Method != Method(kind))
                throw new InvalidDataException("Challenge callback method, invocation or target differs.");
            return kind;
        }
        internal static void Validate(string kind, Type type)
        {
            Method(kind);
            if (type != (kind == "setup" ? typeof(BossSceneController.SetupEventDelegate) : typeof(Action)))
                throw new InvalidDataException("Challenge callback signature differs.");
        }
        internal static Delegate Bind(string kind, object target)
        {
            if (target.GetType() != Target) throw new InvalidDataException("Challenge callback context differs.");
            return Delegate.CreateDelegate(kind == "setup" ? typeof(BossSceneController.SetupEventDelegate) : typeof(Action), target, Method(kind));
        }
        internal static bool IsSlot(Type owner, FieldInfo field, string kind)
            => kind == "setup" ? owner == typeof(CanonicalBossChallengeCarry.Data) && field.DeclaringType == owner && field.Name == "Pending"
                : kind == "complete" && owner == Target && field.DeclaringType == owner && field.Name == "CachedCompletion";
    }
}
