using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Camera position gates Walker activation, so it belongs to gameplay
    // continuation. Bind existing persistent objects; never call SnapTo,
    // SetMode, SceneInit or an FSM entry action while applying the state.
    [Serializable]
    internal sealed class CheckpointCameraState
    {
        public Dictionary<string, string> Controller = null!, Target = null!;
        public Pose[] Transforms = null!;
        public CheckpointPersistentFsmState Fade = null!, Shake = null!;
        private static readonly FieldInfo LockArea = typeof(CameraController).GetField("currentLockArea", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static FieldInfo[] Scalars(Type type) => CheckpointScalarFields.Declared(type).Where(f =>
            f.FieldType.IsPrimitive && f.FieldType != typeof(IntPtr) && f.FieldType != typeof(UIntPtr)
            || f.FieldType.IsEnum || f.FieldType == typeof(string)
            || f.FieldType == typeof(Vector2) || f.FieldType == typeof(Vector3)).ToArray();
        private static readonly FieldInfo[] ControllerFields = Scalars(typeof(CameraController));
        private static readonly FieldInfo[] TargetFields = Scalars(typeof(CameraTarget));

        private static Transform[] Bind(out CameraController controller, out CameraTarget target)
        {
            var cameras = GameCameras.instance ?? throw new InvalidOperationException("Checkpoint cameras are absent.");
            controller = cameras.cameraController; target = cameras.cameraTarget;
            if (controller == null || target == null || cameras.mainCamera == null
                || controller != GameManager.instance.cameraCtrl || controller.cam != cameras.mainCamera
                || controller.camTarget != target || target.cameraCtrl != controller
                || !controller.enabled || !target.enabled
                || controller.lockZoneList == null || controller.lockZoneList.Count != 0
                || (CameraLockArea?)LockArea.GetValue(controller) != null)
                throw new InvalidOperationException("Checkpoint persistent camera bindings or unloaded locks differ: controller="
                    + (controller != null) + ",target=" + (target != null) + ",main=" + (cameras.mainCamera != null)
                    + ",managerAlias=" + (controller == GameManager.instance.cameraCtrl)
                    + ",cameraAlias=" + (controller != null && controller.cam == cameras.mainCamera)
                    + ",targetAlias=" + (controller != null && controller.camTarget == target)
                    + ",controllerAlias=" + (target != null && target.cameraCtrl == controller)
                    + ",enabled=" + (controller != null && controller.enabled) + "/" + (target != null && target.enabled)
                    + ",locks=" + controller?.lockZoneList?.Count
                    + ",liveLock=" + (controller != null && (CameraLockArea?)LockArea.GetValue(controller) != null));
            var chain = new List<Transform>();
            foreach (var leaf in new[] { controller.transform, target.transform })
            {
                var branch = new List<Transform>();
                for (var current = leaf; current != null; current = current.parent)
                {
                    branch.Add(current);
                    if (current == cameras.transform) break;
                    if (branch.Count >= 8) throw new InvalidOperationException("Checkpoint camera hierarchy exceeds its bound.");
                }
                if (branch[branch.Count - 1] != cameras.transform)
                    throw new InvalidOperationException("Checkpoint camera has another persistent root: " + leaf.name);
                branch.Reverse();
                foreach (var transform in branch) if (!chain.Contains(transform)) chain.Add(transform);
            }
            return chain.ToArray();
        }

        internal static CheckpointCameraState Capture()
        {
            var transforms = Bind(out var controller, out var target);
            return new CheckpointCameraState
            {
                Controller = CheckpointScalarFields.Capture(controller, ControllerFields),
                Target = CheckpointScalarFields.Capture(target, TargetFields),
                Fade = CheckpointPersistentFsmState.Capture(GameCameras.instance.cameraFadeFSM),
                Shake = CheckpointPersistentFsmState.Capture(GameCameras.instance.cameraShakeFSM),
                Transforms = transforms.Select(t => new Pose { Name = t.name, Position = t.localPosition,
                    Rotation = t.localRotation, Scale = t.localScale }).ToArray()
            };
        }

        internal Action PrepareApply()
        {
            var transforms = Bind(out var controller, out var target);
            if (Transforms == null || Transforms.Length != transforms.Length
                || Transforms.Where((p, i) => p == null || p.Name != transforms[i].name || !p.Finite()).Any())
                throw new InvalidDataException("Checkpoint camera transform hierarchy or values differ.");
            var applyController = CheckpointScalarFields.PrepareApply(controller, ControllerFields, Controller);
            var applyTarget = CheckpointScalarFields.PrepareApply(target, TargetFields, Target);
            var applyFade = (Fade ?? throw new InvalidDataException("Checkpoint camera fade FSM is absent.")).PrepareApply(GameCameras.instance.cameraFadeFSM);
            var applyShake = (Shake ?? throw new InvalidDataException("Checkpoint camera shake FSM is absent.")).PrepareApply(GameCameras.instance.cameraShakeFSM);
            var cameraIndex = Array.IndexOf(transforms, controller.transform);
            Modding.Logger.Log("[HKTAS] Checkpoint camera before apply=" + controller.transform.position
                + " captured main local=" + Transforms[cameraIndex].Position);
            return () =>
            {
                applyController(); applyTarget();
                applyFade(); applyShake();
                for (var i = 0; i < transforms.Length; i++)
                {
                    transforms[i].localPosition = Transforms[i].Position;
                    transforms[i].localRotation = Transforms[i].Rotation;
                    transforms[i].localScale = Transforms[i].Scale;
                }
            };
        }

        [Serializable]
        internal sealed class Pose
        {
            public string Name = "";
            public Vector3 Position, Scale;
            public Quaternion Rotation;
            internal bool Finite() => new[] { Position.x, Position.y, Position.z, Scale.x, Scale.y, Scale.z,
                Rotation.x, Rotation.y, Rotation.z, Rotation.w }.All(v => !float.IsNaN(v) && !float.IsInfinity(v));
        }
    }
}
