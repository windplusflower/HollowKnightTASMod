using System;
using System.Linq;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace HollowKnightTAS.Core.SceneCheckpoints
{
    internal static class ModRandomConstructorHook
    {
        public static ILHook Install(Func<int> nextSeed, Action<Random> registerInstance)
        {
            var ctor = typeof(Random).GetConstructor(Type.EmptyTypes)
                ?? throw new MissingMethodException(typeof(Random).FullName, ".ctor()");
            return new ILHook(ctor, il =>
            {
                var cursor = new ILCursor(il);
                if (!cursor.TryGotoNext(MoveType.Before, instruction =>
                    instruction.MatchCall(typeof(Environment), "get_TickCount")))
                {
                    throw new InvalidOperationException("Unsupported System.Random constructor.");
                }

                cursor.Remove();
                cursor.EmitDelegate(nextSeed);

                var returns = il.Body.Instructions
                    .Where(instruction => instruction.OpCode == OpCodes.Ret)
                    .ToArray();
                if (returns.Length == 0)
                {
                    throw new InvalidOperationException("Unsupported System.Random constructor (missing ret).");
                }

                foreach (var ret in returns)
                {
                    cursor.Goto(ret, MoveType.AfterLabel);
                    cursor.Emit(OpCodes.Ldarg_0);
                    cursor.EmitDelegate(registerInstance);
                }
            });
        }
    }
}
