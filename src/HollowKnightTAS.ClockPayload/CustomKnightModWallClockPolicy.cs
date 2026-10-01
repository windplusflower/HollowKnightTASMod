using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using HollowKnightTAS.Core.Movie;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.ClockPayload
{
    // V5's first registered Mod adapter. Install before its static root creates
    // SwapManager; reject a late install instead of rewriting an existing timer.
    // Only these two pinned call sites change. BCL clocks remain untouched.
    internal sealed class CustomKnightModWallClockPolicy : IDisposable
    {
        private const string AssemblyName = "CustomKnight";
        private const string AssemblySha256 = "e23d2360f844099093ad84ff3687176c84dc6b4df6f2fcc2ca5fb15cf13098da";
        private readonly List<ILHook> hooks = new List<ILHook>();
        private Assembly? target;
        private string? failure;
        private bool installed;
        private int constructorReads;
        private readonly uint engineThread;

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        internal CustomKnightModWallClockPolicy()
        {
            engineThread = GetCurrentThreadId();
            if (engineThread == 0 || engineThread != ClockController.StartupHookThreadId)
                throw new InvalidOperationException("V5 Mod wall clocks must install on the registered engine thread.");
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == AssemblyName) Install(assembly);
        }

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.GetName().Name != AssemblyName) return;
            try { Install(args.LoadedAssembly); }
            catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; throw; }
        }

        private void Install(Assembly assembly)
        {
            if (target != null)
            {
                if (!ReferenceEquals(target, assembly))
                    throw new InvalidOperationException("V5 found multiple CustomKnight assemblies.");
                return;
            }
            target = assembly;
            string digest;
            using (var file = File.OpenRead(assembly.Location))
            using (var hash = SHA256.Create())
                digest = BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
            if (digest != AssemblySha256)
                throw new InvalidOperationException("V5 CustomKnight wall-clock adapter requires its pinned assembly.");
            var type = assembly.GetType("CustomKnight.SwapManager", throwOnError: true)!;
            var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.FullName, ".ctor");
            var update = type.GetMethod("checkForMissedObjects", BindingFlags.Instance | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.FullName, "checkForMissedObjects");
            if (update.ReturnType != typeof(void))
                throw new InvalidOperationException("V5 CustomKnight timer signature changed.");
            hooks.Add(new ILHook(constructor, il => ReplaceNow(il, true)));
            hooks.Add(new ILHook(update, il => ReplaceNow(il, false)));
            installed = true;
        }

        private void ReplaceNow(ILContext il, bool constructor)
        {
            var cursor = new ILCursor(il);
            var calls = 0;
            while (cursor.TryGotoNext(MoveType.Before, instruction => instruction.MatchCall(typeof(DateTime), "get_Now")))
            {
                // Preserve the original instruction because a branch can target
                // the DateTime.Now call (the pinned update method does this).
                cursor.Next.OpCode = OpCodes.Nop;
                cursor.Next.Operand = null;
                cursor.Index++;
                cursor.EmitDelegate<Func<DateTime>>(() => ReadNow(constructor));
                calls++;
            }
            if (calls != 1)
                throw new InvalidOperationException("V5 CustomKnight timer has an unexpected DateTime.Now call count.");
        }

        private DateTime ReadNow(bool constructor)
        {
            if (GetCurrentThreadId() != engineThread)
                throw new InvalidOperationException("V5 Mod wall-clock reads require the registered engine thread.");
            var result = ModWallClock.LocalNow(Time.realtimeSinceStartupAsDouble);
            if (constructor) Interlocked.Increment(ref constructorReads);
            return result;
        }

        internal void Verify()
        {
            if (failure != null) throw new InvalidOperationException("V5 Mod wall-clock installation failed: " + failure);
            if (!installed || Volatile.Read(ref constructorReads) < 1)
                throw new InvalidOperationException("V5 requires its pinned CustomKnight constructor to run through the wall-clock policy.");
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            for (var index = hooks.Count - 1; index >= 0; index--) hooks[index].Dispose();
            hooks.Clear();
        }
    }
}
