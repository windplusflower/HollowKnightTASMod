using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalMapKeyParentDirectory
    {
        internal const string ContainerPrefix = "$/FadeGroup/container/";
        internal static readonly string[] ContainerNames = { "animators", "spriteRenderers", "texts" };
        internal static readonly FieldInfo Raw = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;
        internal readonly Transform Keys;
        internal readonly Transform[] Parents;
        internal readonly PlayMakerFSM Control;
        internal readonly FadeGroup Fade;
        internal readonly Fsm Fsm;
        internal readonly Array[] Containers;
        internal readonly SortedDictionary<string, UnityEngine.Object> Native = new SortedDictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
        internal CanonicalMapKeyParentDirectory(Transform keys)
        {
            Keys = keys;
            CanonicalMapKeyTextDirectory.Build(keys);
            var root = keys.parent;
            if (root == null || !root.GetComponents<Component>().Select(value => value.GetType()).SequenceEqual(new[] { typeof(Transform), typeof(PlayMakerFSM), typeof(FadeGroup) }))
                throw new InvalidDataException("Original Map Key dependency root component directory differs.");
            Control = root.GetComponent<PlayMakerFSM>(); Fade = root.GetComponent<FadeGroup>();
            Fsm = Raw.GetValue(Control) as Fsm ?? throw new InvalidDataException("Original Map Key dependency Control is absent.");
            if ((string?)typeof(Fsm).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Fsm) != "Control")
                throw new InvalidDataException("Original Map Key Control identity differs.");
            Native.Add("$", root.gameObject); Native.Add("$:UnityEngine.Transform[0]", root);
            Native.Add("$:PlayMakerFSM[0]", Control); Native.Add("$:FadeGroup[0]", Fade);
            Parents = new Transform[9];
            for (var index = 0; index < 9; index++) {
                var key = CanonicalMapKeyTextDirectory.Keys[index]; var parent = keys.Find(key);
                if (parent == null || !parent.GetComponents<Component>().Select(value => value.GetType()).SequenceEqual(new[] { typeof(Transform) }))
                    throw new InvalidDataException("Original Key parent has an additional callback owner: " + key);
                var children = Enumerable.Range(0, parent.childCount).Select(parent.GetChild).ToArray();
                var icon = children.SingleOrDefault(child => child.name == "Pin Icon");
                if (children.Length != 2 || icon == null || icon.childCount != 0
                    || !icon.GetComponents<Component>().Select(value => value.GetType()).SequenceEqual(new[] { typeof(Transform), typeof(SpriteRenderer) }))
                    throw new InvalidDataException("Original Key parent callback subtree differs: " + key);
                Parents[index] = parent;
                Native.Add("$/" + key, parent.gameObject); Native.Add("$/" + key + ":UnityEngine.Transform[0]", parent);
            }
            Containers = ContainerNames.Select(name => (Array)(typeof(FadeGroup).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(Fade)
                ?? throw new InvalidDataException("Original FadeGroup constructor array is absent."))).ToArray();
            RequireContainers();
        }
        internal void RequireContainers()
        {
            if (Containers[0].GetType() != typeof(InvAnimateUpAndDown[]) || Containers[0].Length != 0
                || Containers[1].GetType() != typeof(SpriteRenderer[]) || Containers[1].Length != 11
                || Containers[2].GetType() != typeof(TextMeshPro[]) || Containers[2].Length != 11)
                throw new InvalidDataException("Original FadeGroup complete constructor array ABI differs.");
            var names = new[] { "Bench" }.Concat(CanonicalMapKeyTextDirectory.Keys).Concat(new[] { "Vendor" }).ToArray();
            for (var index = 0; index < names.Length; index++) {
                var parent = Keys.Find(names[index]) ?? throw new InvalidDataException("Original FadeGroup actual Key owner is absent.");
                var text = parent.Find("Text"); var icon = parent.Find(index == 0 ? "pin_bench" : "Pin Icon");
                if (text == null || icon == null || !ReferenceEquals(Containers[2].GetValue(index), text.GetComponent<TextMeshPro>())
                    || !ReferenceEquals(Containers[1].GetValue(index), icon.GetComponent<SpriteRenderer>()))
                    throw new InvalidDataException("Original FadeGroup source array does not bind the exact real owner/index: " + names[index]);
            }
            for (var index = 0; index < ContainerNames.Length; index++)
                if (!ReferenceEquals(Containers[index], typeof(FadeGroup).GetField(ContainerNames[index])!.GetValue(Fade)))
                    throw new InvalidOperationException("Actual current FadeGroup container reference changed.");
        }
        internal void RequireQuiet()
        {
            var fadeMvid = typeof(FadeGroup).Module.ModuleVersionId;
            if (fadeMvid != new Guid("2b54cdaf-7a93-4f0e-b297-d50c4090ea9f"))
                CanonicalSceneMappingCarry.WarnOnce("MapKeyParentDirectory:FadeMvid:" + fadeMvid,
                    "[HKTAS] FadeGroup ModuleVersionId differs from pinned original (continuing): actual=" + fadeMvid.ToString("D"));
            if ((int)typeof(FadeGroup).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Fade)! != 0)
                throw new InvalidOperationException("An active source/target FadeGroup continuation needs a future explicit contract.");
            var state = typeof(Fsm).GetField("activeState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Fsm) as FsmState;
            var name = state == null ? null : (string?)typeof(FsmState).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state);
            if (name != "Inactive" && name != "No Pins")
                throw new InvalidOperationException("An active Map Key Control continuation needs a future explicit contract.");
            RequireContainers();
        }
    }
}
