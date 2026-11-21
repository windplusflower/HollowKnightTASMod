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
        private static bool abiMismatchWarned;

        internal static void Request(int kind)
        {
            var matches = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload").ToArray();
            if (matches.Length == 0) throw new InvalidOperationException("Actual random producer payload is absent.");
            if (matches.Length > 1 && !abiMismatchWarned)
            {
                abiMismatchWarned = true;
                Modding.Logger.LogWarn("[HKTAS] Multiple HollowKnightTAS.ClockPayload assemblies loaded; using first instance.");
            }
            var type = matches[0].GetType("HollowKnightTAS.ClockPayload.ClockController", true)!;
            var abi = type.GetMethod("GetResidentRandomProducerAbi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            var request = type.GetMethod("RequestResidentRandom", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
            if (request == null || request.ReturnType != typeof(void))
                throw new MissingMethodException("HollowKnightTAS.ClockPayload.ClockController", "RequestResidentRandom");
            if (abi == null || abi.ReturnType != typeof(int))
            {
                if (!abiMismatchWarned)
                {
                    abiMismatchWarned = true;
                    Modding.Logger.LogWarn("[HKTAS] ClockPayload.GetResidentRandomProducerAbi is missing; proceeding with RequestResidentRandom.");
                }
            }
            else
            {
                var abiVersion = (int)abi.Invoke(null, null)!;
                if (abiVersion != 1 && !abiMismatchWarned)
                {
                    abiMismatchWarned = true;
                    Modding.Logger.LogWarn("[HKTAS] ClockPayload ResidentRandomProducer ABI differs (expected=1, actual=" + abiVersion + "); proceeding.");
                }
            }
            try { request.Invoke(null, new object[] { kind }); }
            catch (TargetInvocationException exception) when (exception.InnerException != null) { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
        }
    }
}
