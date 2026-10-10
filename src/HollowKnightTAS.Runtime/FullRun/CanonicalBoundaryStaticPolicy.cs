using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalBoundaryStaticPolicy
    {
        internal static void ResetExecutionStack()
        {
            var fields = typeof(FsmExecutionStack).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(field => !field.IsLiteral).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            if (fields.Length != 2 || fields[0].Name != "<MaxStackCount>k__BackingField" || fields[0].FieldType != typeof(int)
                || fields[1].Name != "fsmExecutionStack" || fields[1].FieldType != typeof(Stack<Fsm>)
                || !(fields[1].GetValue(null) is Stack<Fsm> stack) || stack.Count != 0)
                throw new InvalidOperationException("Canonical boundary is inside an FSM execution stack or its closed fields differ.");
            // The original stack is private; only Push/Pop use it. At B no FSM
            // action is executing. Discard its version, unused storage and
            // debug-only maximum depth, without touching any live FSM state.
            fields[1].SetValue(null, new Stack<Fsm>(256));
            fields[0].SetValue(null, 0);
            if (FsmExecutionStack.StackCount != 0 || FsmExecutionStack.MaxStackCount != 0)
                throw new InvalidOperationException("Canonical FSM execution stack readback differs.");
        }
    }
}
