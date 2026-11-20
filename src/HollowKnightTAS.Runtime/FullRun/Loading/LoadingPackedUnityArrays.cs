using System;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class LoadingPackedUnityArrays
    {
        internal static bool Enabled => Environment.GetEnvironmentVariable("HKTAS_LOADING_ORACLE_LEGACY_ARRAYS") != "1";
        internal static bool Registered(Type type) => type == typeof(Vector2) || type == typeof(Vector3)
            || type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color)
            || type == typeof(Color32) || type == typeof(Matrix4x4) || type == typeof(BoneWeight)
            || type == typeof(UIVertex) || type == typeof(UICharInfo) || type == typeof(UILineInfo)
            || type == typeof(ParticleSystem.Particle) || type == typeof(Keyframe);
    }
}
