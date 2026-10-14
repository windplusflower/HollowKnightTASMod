using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Read-only bounded evidence for the first real loading-loop RNG delta.
    // Always call each original once and return its actual result unchanged.
    internal sealed class LoadingSuffixTweenCausality : IDisposable
    {
        private delegate string OriginalId();
        private delegate void OriginalLaunch(GameObject target, Hashtable args);
        private delegate void OriginalCharmUpdate(CharmDisplay owner);
        private readonly Hook idHook, launchHook, charmHook;
        private readonly Func<object> snapshot;
        private readonly Func<UnityEngine.Object, string> identity;
        private readonly MemoryStream output = new MemoryStream();
        private string? failure;
        private int rows;
        internal LoadingSuffixTweenCausality(Func<object> snapshot, Func<UnityEngine.Object, string> identity)
        {
            this.snapshot = snapshot; this.identity = identity;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var id = typeof(iTween).GetMethod("GenerateID", flags, null, Type.EmptyTypes, null)!;
            var launch = typeof(iTween).GetMethod("Launch", flags, null, new[] { typeof(GameObject), typeof(Hashtable) }, null)!;
            if (id == null || launch == null || id.ReturnType != typeof(string) || launch.ReturnType != typeof(void)
                || typeof(iTween).Module.ModuleVersionId != typeof(GameManager).Module.ModuleVersionId)
                throw new InvalidOperationException("Original iTween causality reader ABI differs.");
            idHook = new Hook(id, (Func<OriginalId, string>)Generate);
            launchHook = new Hook(launch, (Action<OriginalLaunch, GameObject, Hashtable>)Launch);
            var update = typeof(CharmDisplay).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                ?? throw new MissingMethodException("CharmDisplay.FixedUpdate");
            charmHook = new Hook(update, (Action<OriginalCharmUpdate, CharmDisplay>)CharmUpdate);
        }
        private object? Read()
        {
            try { return snapshot(); } catch (Exception error) { failure = error.Message; return null; }
        }
        private void Record(object row)
        {
            try {
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(row) + "\n");
                if (rows++ >= 256 || output.Length + bytes.Length > 1024 * 1024)
                    throw new InvalidOperationException("Suffix tween causality diagnostic exceeded its bound.");
                output.Write(bytes, 0, bytes.Length);
            } catch (Exception error) { failure = error.Message; }
        }
        private string Generate(OriginalId original)
        {
            var before = Read();
            var result = original();
            var after = Read();
            try {
                var callers = new StackTrace().GetFrames()?.Take(32).Select(frame => frame.GetMethod())
                    .Where(method => method != null).Select(method => method!.DeclaringType?.FullName + "." + method.Name).ToArray();
                Record(new { diagnosticOnly = true, kind = "generate-id", id = result, before, after, callers });
            } catch (Exception error) { failure = error.Message; }
            return result;
        }
        private void Launch(OriginalLaunch original, GameObject target, Hashtable args)
        {
            var before = Read(); string? owner = null;
            try { owner = identity(target); } catch (Exception error) { failure = error.Message; }
            original(target, args);
            Record(new { diagnosticOnly = true, kind = "launch", id = args["id"] as string, owner, before, after = Read() });
        }
        private void CharmUpdate(OriginalCharmUpdate original, CharmDisplay owner)
        {
            var before = Read(); original(owner); var after = Read();
            if (JsonConvert.SerializeObject(before) == JsonConvert.SerializeObject(after)) return;
            try { Record(new { diagnosticOnly = true, kind = "charm-fixed", owner = identity(owner), before, after }); }
            catch (Exception error) { failure = error.Message; }
        }
        internal void RequireHealthy()
        {
            if (failure != null) throw new InvalidOperationException("Suffix tween causality reader failed: " + failure);
        }
        internal void Export(string directory)
        {
            RequireHealthy();
            using var file = new FileStream(Path.Combine(directory, "suffix-tween-causality.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            output.WriteTo(file);
        }
        public void Dispose() { idHook.Dispose(); launchHook.Dispose(); charmHook.Dispose(); output.Dispose(); }
    }
}
