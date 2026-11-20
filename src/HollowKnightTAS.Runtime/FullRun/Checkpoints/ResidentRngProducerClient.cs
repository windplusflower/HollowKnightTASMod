using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Stateless caller. The authority and current fence are owned by the
    // already-running payload, shared by legacy roots and full-run scenes.
    internal static class ResidentRngProducerClient
    {
        internal static void Request(int kind)
        {
            var matches = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload").ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Actual random producer payload is absent or ambiguous.");
            var type = matches[0].GetType("HollowKnightTAS.ClockPayload.ClockController", true)!;
            var abi = type.GetMethod("GetResidentRandomProducerAbi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            var request = type.GetMethod("RequestResidentRandom", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
            if (abi == null || abi.ReturnType != typeof(int) || request == null || request.ReturnType != typeof(void)
                || (int)abi.Invoke(null, null)! != 1) throw new InvalidOperationException("Update ClockPayload: shared random producer admission is unavailable.");
            try { request.Invoke(null, new object[] { kind }); }
            catch (TargetInvocationException exception) when (exception.InnerException != null) { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
        }
    }
}
