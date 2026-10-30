using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The gameplay portion of D. Pool actors, visual FSMs, Tween history and
    // arbitrary broadphase history deliberately have no slots in this data.
    // This adapter only claims its enumerated scalar/native fields; remaining
    // references and native body internals still require the coverage gate.
    internal static class CanonicalGameplayCarry
    {
        internal const string Root = "D/Gameplay";
        private static readonly FieldInfo History = typeof(HeroController).GetField("positionHistory", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("Original Hero position history field is absent.");
        private static FieldInfo[] Scalars(Type type) => CheckpointScalarFields.Declared(type).Where(field =>
            field.FieldType.IsPrimitive && field.FieldType != typeof(IntPtr) && field.FieldType != typeof(UIntPtr)
            || field.FieldType.IsEnum || field.FieldType == typeof(string)
            || field.FieldType == typeof(Vector2) || field.FieldType == typeof(Vector3)).ToArray();

        internal static readonly Type[] DataTypes = { typeof(Data), typeof(ScalarOwner), typeof(ScalarOwner[]),
            typeof(Pose), typeof(Pose[]), typeof(string[]), typeof(Vector2[]), typeof(RigidbodyType2D) };

        internal static IReadOnlyDictionary<string, string> CaptureHeroPreservationScalars()
        {
            Bind(out var hero, out var body);
            var result = CheckpointScalarFields.Capture(hero, Scalars(typeof(HeroController)));
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            var physics = new LoadingRigidbodyObservation();
            var native = new HeroNativeValues { Position = hero.transform.localPosition, Rotation = hero.transform.localRotation,
                Scale = hero.transform.localScale, BodyType = body.bodyType, Velocity = physics.CaptureCheckpointVelocity(body),
                Gravity = body.gravityScale, Simulated = body.simulated };
            foreach (var item in CheckpointScalarFields.Capture(native, CheckpointScalarFields.Declared(typeof(HeroNativeValues)))) result.Add(item.Key, item.Value);
            var history = History.GetValue(hero) as Vector2[];
            if (history?.Length > 1024) throw new InvalidDataException("Hero preservation history bound exceeded.");
            result.Add("HeroHistory/length", history == null ? "null" : history.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (history != null)
                for (var index = 0; index < history.Length; index++) {
                    result.Add("HeroHistory/" + index + "/x", BitConverter.ToUInt32(BitConverter.GetBytes(history[index].x), 0).ToString("x8"));
                    result.Add("HeroHistory/" + index + "/y", BitConverter.ToUInt32(BitConverter.GetBytes(history[index].y), 0).ToString("x8"));
                }
            return result;
        }
        internal static object[] HeroPreservationOwners()
        {
            Bind(out var hero, out var body);
            return new[] { (object)hero, body, hero.transform, History.GetValue(hero) ?? DBNull.Value };
        }
        private sealed class HeroNativeValues
        {
            public Vector3 Position, Scale;
            public Quaternion Rotation;
            public RigidbodyType2D BodyType;
            public Vector2 Velocity;
            public float Gravity;
            public bool Simulated;
        }

        // Own DTO layouts participate in the graph schema, independently of
        // the frozen original assembly field registry.
        internal static string SchemaIdentity => string.Join("\n", DataTypes.OrderBy(CanonicalCarryGraphCodec.Identity, StringComparer.Ordinal)
            .Select(type => CanonicalCarryGraphCodec.Identity(type) + ":" + string.Join(";", type.IsArray || type.IsEnum ? Array.Empty<string>()
                : CanonicalCarryGraphCodec.Fields(type).Select(field => CanonicalCarryGraphCodec.Identity(field.DeclaringType!) + "/" + field.Name
                    + "=" + CanonicalCarryGraphCodec.Identity(field.FieldType)))));

        private static SortedDictionary<string, object> Bind(out HeroController hero, out Rigidbody2D body)
        {
            var manager = GameManager.instance ?? throw new InvalidDataException("Gameplay GameManager is absent.");
            hero = HeroController.SilentInstance ?? throw new InvalidDataException("Gameplay Hero is absent.");
            body = hero.GetComponent<Rigidbody2D>() ?? throw new InvalidDataException("Gameplay Hero body is absent.");
            var cameras = GameCameras.instance ?? throw new InvalidDataException("Gameplay cameras are absent.");
            var controller = cameras.cameraController;
            var target = cameras.cameraTarget;
            var player = PlayerData.instance;
            if (player == null || manager.sceneData == null || hero.cState == null
                || !ReferenceEquals(manager.playerData, player) || !ReferenceEquals(hero.playerData, player)
                || hero.transform.parent != null || controller == null || target == null || cameras.mainCamera == null
                || controller != manager.cameraCtrl || controller.cam != cameras.mainCamera
                || controller.camTarget != target || target.cameraCtrl != controller)
                throw new InvalidDataException("Gameplay persistent root aliases differ.");
            return new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["CameraController"] = controller, ["CameraTarget"] = target, ["GameManager"] = manager, ["HeroController"] = hero };
        }

        private static Transform[] Transforms(HeroController hero)
        {
            var cameras = GameCameras.instance;
            var result = new List<Transform> { hero.transform };
            foreach (var leaf in new[] { cameras.cameraController.transform, cameras.cameraTarget.transform })
            {
                var branch = new List<Transform>();
                for (var current = leaf; current != null; current = current.parent)
                {
                    if (branch.Count == 8) throw new InvalidDataException("Gameplay camera transform chain exceeds its bound.");
                    branch.Add(current);
                    if (current == cameras.transform) break;
                }
                if (branch.Count == 0 || branch[branch.Count - 1] != cameras.transform)
                    throw new InvalidDataException("Gameplay camera transform belongs to another root.");
                branch.Reverse();
                foreach (var item in branch) if (!result.Contains(item)) result.Add(item);
            }
            return result.ToArray();
        }

        internal static Data Capture(BoundaryObjectIdentity identities)
        {
            var owners = Bind(out var hero, out var body);
            var physics = new LoadingRigidbodyObservation();
            var history = History.GetValue(hero) as Vector2[];
            if (history?.Length > 1024) throw new InvalidDataException("Gameplay Hero history exceeds its bound.");
            var data = new Data {
                Owners = owners.Select(pair => {
                    var fields = CheckpointScalarFields.Capture(pair.Value, Scalars(pair.Value.GetType()));
                    return new ScalarOwner { Binding = pair.Key, Identity = identities.Get((UnityEngine.Object)pair.Value),
                        Fields = fields.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                        Values = fields.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray() };
                }).ToArray(),
                Transforms = Transforms(hero).Select(transform => new Pose {
                    Identity = identities.Get(transform), Parent = transform.parent == null ? "" : identities.Get(transform.parent),
                    Position = transform.localPosition, Rotation = transform.localRotation, Scale = transform.localScale }).ToArray(),
                BodyIdentity = identities.Get(body), BodyType = body.bodyType,
                BodyVelocity = physics.CaptureCheckpointVelocity(body), GravityScale = body.gravityScale, Simulated = body.simulated,
                History = history == null ? null : (Vector2[])history.Clone()
            };
            ValidateNative(data);
            return data;
        }

        private static void ValidateNative(Data data)
        {
            if (data.Version != 1 || data.Owners == null || data.Owners.Length != 4
                || data.Transforms == null || data.Transforms.Length == 0 || data.Transforms.Length > 17
                || !Enum.IsDefined(typeof(RigidbodyType2D), data.BodyType) || string.IsNullOrEmpty(data.BodyIdentity)
                || data.History?.Length > 1024)
                throw new InvalidDataException("Gameplay data schema or native bounds differ.");
            var values = new List<float> { data.BodyVelocity.x, data.BodyVelocity.y, data.GravityScale };
            foreach (var pose in data.Transforms)
            {
                if (pose == null || string.IsNullOrEmpty(pose.Identity) || pose.Parent == null)
                    throw new InvalidDataException("Gameplay transform identity is absent.");
                values.AddRange(new[] { pose.Position.x, pose.Position.y, pose.Position.z, pose.Rotation.x, pose.Rotation.y,
                    pose.Rotation.z, pose.Rotation.w, pose.Scale.x, pose.Scale.y, pose.Scale.z });
            }
            foreach (var point in data.History ?? Array.Empty<Vector2>()) { values.Add(point.x); values.Add(point.y); }
            if (values.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
                throw new InvalidDataException("Gameplay native values are not finite.");
        }

        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var owner = view.Root(Root); var type = typeof(Data);
            object? Field(string name) => view.Field(owner, type, name);
            var data = new Data { Version = (int)Field(nameof(Data.Version))!,
                Owners = (ScalarOwner[])Field(nameof(Data.Owners))!, Transforms = (Pose[])Field(nameof(Data.Transforms))!,
                BodyIdentity = (string)Field(nameof(Data.BodyIdentity))!, BodyType = (RigidbodyType2D)Field(nameof(Data.BodyType))!,
                BodyVelocity = (Vector2)Field(nameof(Data.BodyVelocity))!, GravityScale = (float)Field(nameof(Data.GravityScale))!,
                Simulated = (bool)Field(nameof(Data.Simulated))!, History = (Vector2[]?)Field(nameof(Data.History)) };
            ValidateNative(data); return data;
        }

        internal static Action PrepareApply(Data input, BoundaryObjectIdentity identities)
        {
            // The caller supplies the already frozen/decoded graph root.
            // Resolve all bindings and scalar layouts before any live write.
            ValidateNative(input);
            var owners = Bind(out var hero, out var body);
            if (!input.Owners.Select(owner => owner?.Binding).SequenceEqual(owners.Keys)
                || identities.Get(body) != input.BodyIdentity)
                throw new InvalidDataException("Gameplay owner or body identity differs.");
            var applications = input.Owners.Select(saved => {
                var owner = owners[saved.Binding];
                if (saved.Identity != identities.Get((UnityEngine.Object)owner) || saved.Fields == null || saved.Values == null
                    || saved.Fields.Length > 2048 || saved.Fields.Length != saved.Values.Length
                    || saved.Fields.Distinct(StringComparer.Ordinal).Count() != saved.Fields.Length)
                    throw new InvalidDataException("Gameplay scalar owner binding or bounds differ.");
                var fields = saved.Fields.Select((key, index) => new { key, value = saved.Values[index] })
                    .ToDictionary(pair => pair.key, pair => pair.value, StringComparer.Ordinal);
                return CheckpointScalarFields.PrepareApply(owner, Scalars(owner.GetType()), fields);
            }).ToArray();
            var transforms = Transforms(hero);
            if (input.Transforms.Length != transforms.Length || input.Transforms.Where((pose, index) =>
                pose.Identity != identities.Get(transforms[index]) || pose.Parent != (transforms[index].parent == null ? "" : identities.Get(transforms[index].parent))).Any())
                throw new InvalidDataException("Gameplay transform topology differs.");
            var history = input.History == null ? null : (Vector2[])input.History.Clone();
            var poses = input.Transforms.Select(pose => new Pose { Position = pose.Position, Rotation = pose.Rotation, Scale = pose.Scale }).ToArray();
            var bodyType = input.BodyType; var velocity = input.BodyVelocity; var gravity = input.GravityScale; var simulated = input.Simulated;
            var consumed = false;
            return () => {
                if (consumed) throw new InvalidOperationException("Gameplay preparation was already consumed.");
                var currentOwners = Bind(out var currentHero, out var currentBody);
                var currentTransforms = Transforms(currentHero);
                if (!ReferenceEquals(hero, currentHero) || !ReferenceEquals(body, currentBody)
                    || owners.Any(pair => !ReferenceEquals(pair.Value, currentOwners[pair.Key]))
                    || currentTransforms.Length != transforms.Length
                    || currentTransforms.Where((item, index) => !ReferenceEquals(item, transforms[index])).Any())
                    throw new InvalidOperationException("Gameplay persistent owners changed before application.");
                consumed = true;
                foreach (var application in applications) application();
                History.SetValue(hero, history);
                for (var index = 0; index < transforms.Length; index++) {
                    var pose = poses[index];
                    transforms[index].localPosition = pose.Position; transforms[index].localRotation = pose.Rotation; transforms[index].localScale = pose.Scale;
                }
                if (body.bodyType != bodyType) body.bodyType = bodyType;
                body.gravityScale = gravity; body.simulated = simulated; body.velocity = velocity;
                var heroBox = currentHero.transform.Find("HeroBox");
                if (heroBox != null)
                {
                    var heroBoxCol = heroBox.GetComponent<BoxCollider2D>();
                    if (heroBoxCol != null)
                    {
                        heroBoxCol.offset = new Vector2(0.005573273f, -0.70312494f);
                        heroBoxCol.size = new Vector2(0.45541382f, 1.1875f);
                    }
                }
                // The original body/property writers can update the tree and
                // native caches. Their complete readback remains mandatory.
            };
        }

        internal sealed class Data
        {
            public int Version = 1;
            public ScalarOwner[] Owners = Array.Empty<ScalarOwner>();
            public Pose[] Transforms = Array.Empty<Pose>();
            public string BodyIdentity = "";
            public RigidbodyType2D BodyType;
            public Vector2 BodyVelocity;
            public float GravityScale;
            public bool Simulated;
            public Vector2[]? History;
        }
        internal sealed class ScalarOwner
        {
            public string Binding = "", Identity = "";
            public string[] Fields = Array.Empty<string>(), Values = Array.Empty<string>();
        }
        internal sealed class Pose
        {
            public string Identity = "", Parent = "";
            public Vector3 Position, Scale;
            public Quaternion Rotation;
        }
    }
}
