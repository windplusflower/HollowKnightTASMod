using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Passive diagnostic IL taps around existing calls. Arguments, results and RNG
    // state are left untouched. Coverage is Assembly-CSharp only, not native/Mod RNG.
    internal sealed class LoadingRandomCallTrace : IDisposable
    {
        private readonly List<ILHook> hooks = new List<ILHook>();
        private Hook? updateHook;
        private readonly List<Hook> lifecycleHooks = new List<Hook>();
        private delegate void OriginalUpdate(PlayMakerFSM self);
        private readonly Action<string, string, object?> sample;
        private static readonly FieldInfo WaitTimer = typeof(HutongGames.PlayMaker.Actions.Wait).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        public int CallSites { get; private set; }

        public LoadingRandomCallTrace(Action<string, string, object?> sample)
        {
            this.sample = sample;
            try
            {
                var assembly = typeof(HeroController).Assembly;
                using (var definition = AssemblyDefinition.ReadAssembly(assembly.Location))
                foreach (var type in definition.MainModule.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.Instructions.Any(IsRandomCall)) continue;
                    if (method.HasGenericParameters || type.HasGenericParameters)
                    {
                        Modding.Logger.Log("[HKTAS] Loading RNG diagnostic excludes generic caller: " + method.FullName);
                        continue;
                    }
                    var reflected = assembly.ManifestModule.ResolveMethod(method.MetadataToken.ToInt32());
                    hooks.Add(new ILHook(reflected, il =>
                    {
                        var calls = il.Body.Instructions.Where(IsRandomCall).ToArray();
                        foreach (var call in calls)
                        {
                            var site = method.FullName + "@" + call.Offset.ToString("x4") + ":" + ((MethodReference)call.Operand).FullName;
                            var cursor = new ILCursor(il);
                            cursor.Goto(call, MoveType.Before);
                            if (method.IsStatic || type.IsValueType) cursor.Emit(OpCodes.Ldnull); else cursor.Emit(OpCodes.Ldarg_0);
                            cursor.EmitDelegate<Action<object?>>(self => sample("rng-before", site, self));
                            cursor.Goto(call, MoveType.After);
                            if (method.IsStatic || type.IsValueType) cursor.Emit(OpCodes.Ldnull); else cursor.Emit(OpCodes.Ldarg_0);
                            cursor.EmitDelegate<Action<object?>>(self => sample("rng-after", site, self));
                            CallSites++;
                        }
                    }));
                }
                Modding.Logger.Log("[HKTAS] Loading RNG diagnostic attached: " + hooks.Count + " methods, " + CallSites + " calls.");
                On.HutongGames.PlayMaker.Actions.Wait.OnEnter += WaitEnter;
                On.HutongGames.PlayMaker.Actions.Wait.OnUpdate += WaitUpdate;
                var update = typeof(PlayMakerFSM).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!;
                updateHook = new Hook(update, (Action<OriginalUpdate, PlayMakerFSM>)ObserveUpdate);
                foreach (var name in new[] { "OnEnable", "Start" })
                {
                    var lifecycle = typeof(PlayMakerFSM).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
                    lifecycleHooks.Add(new Hook(lifecycle, (Action<OriginalUpdate, PlayMakerFSM>)((original, self) =>
                    {
                        var selected = self.FsmName == "Bouncer Control"
                            && (self.gameObject.name == "Bursting Bouncer (4)" || self.gameObject.name == "Bursting Bouncer (5)");
                        if (selected) sample("fsm-lifecycle-before", name + "/component=" + self.GetInstanceID()
                            + "/gameObject=" + self.gameObject.GetInstanceID() + "/state=" + self.ActiveStateName, self);
                        if (selected && name == "OnEnable") sample("fsm-enable-caller",
                            new System.Diagnostics.StackTrace(false).ToString(), self);
                        original(self);
                        if (selected) sample("fsm-lifecycle-after", name + "/state=" + self.ActiveStateName, self);
                    })));
                }
            }
            catch { Dispose(); throw; }
        }

        private void SampleWait(string phase, HutongGames.PlayMaker.Actions.Wait self)
        {
            if (self.Fsm?.Name == "Initial Activation" && self.Fsm.GameObject?.name == "Charms")
                sample(phase, "Charms.InitialActivation.Wait", self);
        }
        private void ObserveUpdate(OriginalUpdate original, PlayMakerFSM self)
        {
            var selected = self.FsmName == "Bouncer Control"
                && (self.gameObject.name == "Bursting Bouncer (4)" || self.gameObject.name == "Bursting Bouncer (5)");
            if (selected) sample("fsm-update-before", self.FsmName + "/" + self.ActiveStateName, self);
            original(self);
            if (selected) sample("fsm-update-after", self.FsmName + "/" + self.ActiveStateName, self);
        }

        private void WaitEnter(On.HutongGames.PlayMaker.Actions.Wait.orig_OnEnter original, HutongGames.PlayMaker.Actions.Wait self)
        {
            original(self);
            SampleWait("wait-enter", self);
        }

        private void WaitUpdate(On.HutongGames.PlayMaker.Actions.Wait.orig_OnUpdate original, HutongGames.PlayMaker.Actions.Wait self)
        {
            SampleWait("wait-before", self);
            original(self);
            SampleWait("wait-after", self);
        }

        private static bool IsRandomCall(Instruction instruction) =>
            (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
            && instruction.Operand is MethodReference method
            && method.DeclaringType.FullName == "UnityEngine.Random"
            && method.Name != "get_state";

        public static string Describe(object? self)
        {
            if (self is FsmStateAction action)
                return (action.Fsm?.GameObject?.name ?? "") + "/" + action.Fsm?.Name + "/" + action.State?.Name
                    + (self is HutongGames.PlayMaker.Actions.Wait wait ? ":timerBits=" + BitConverter.ToInt32(BitConverter.GetBytes((float)WaitTimer.GetValue(wait)), 0).ToString("x8") : "");
            if (self is Component component) return component == null ? "destroyed" : component.gameObject.name;
            return self?.GetType().FullName ?? "static";
        }

        public void Dispose()
        {
            updateHook?.Dispose(); updateHook = null;
            foreach (var hook in lifecycleHooks) hook.Dispose(); lifecycleHooks.Clear();
            On.HutongGames.PlayMaker.Actions.Wait.OnEnter -= WaitEnter;
            On.HutongGames.PlayMaker.Actions.Wait.OnUpdate -= WaitUpdate;
            for (var i = hooks.Count - 1; i >= 0; i--) hooks[i].Dispose();
            hooks.Clear();
        }
    }
}
