using System;
using System.Text;

namespace HollowKnightTAS.Core.SceneCheckpoints
{
    /// <summary>
    /// Computes deterministic boundary RNG seeds from boundary ordinal, target scene name, and entry gate name.
    /// </summary>
    public static class LeanS0Seed
    {
        private const uint FnvOffsetBasis = 2166136261u;
        private const uint FnvPrime = 16777619u;
        private const string DomainTag = "hktas-lean-s0-seed-v1";

        /// <summary>
        /// Computes a stable 32-bit seed for <c>UnityEngine.Random.InitState</c> at an S0 boundary.
        /// </summary>
        public static int ComputeBoundarySeed(int boundaryOrdinal, string targetSceneName, string? entryGateName)
        {
            if (boundaryOrdinal <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(boundaryOrdinal), "Boundary ordinal must be positive.");
            }
            if (string.IsNullOrWhiteSpace(targetSceneName))
            {
                throw new ArgumentException("Target scene name is required.", nameof(targetSceneName));
            }

            uint hash = FnvOffsetBasis;
            AppendString(ref hash, DomainTag);
            AppendByte(ref hash, 0xff);
            AppendInt32(ref hash, boundaryOrdinal);
            AppendByte(ref hash, 0xfe);
            AppendString(ref hash, targetSceneName);
            AppendByte(ref hash, 0xfd);
            AppendString(ref hash, entryGateName ?? string.Empty);
            int seed = unchecked((int)hash);
            return seed == 0 ? 1 : seed;
        }

        private static void AppendString(ref uint hash, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            AppendInt32(ref hash, bytes.Length);
            for (int i = 0; i < bytes.Length; i++)
            {
                AppendByte(ref hash, bytes[i]);
            }
        }

        private static void AppendInt32(ref uint hash, int value)
        {
            uint bits = unchecked((uint)value);
            AppendByte(ref hash, (byte)bits);
            AppendByte(ref hash, (byte)(bits >> 8));
            AppendByte(ref hash, (byte)(bits >> 16));
            AppendByte(ref hash, (byte)(bits >> 24));
        }

        private static void AppendByte(ref uint hash, byte value)
        {
            hash ^= value;
            hash *= FnvPrime;
        }
    }

    /// <summary>
    /// Coordinates target-scene RNG seed handle synchronization between S0 boundary seeding
    /// and legacy <c>RandomSeedHandshake</c> so loading/bootstrap scenes never consume the flag prematurely.
    /// </summary>
    public static class LeanS0RandomSeedSync
    {
        /// <summary>
        /// Updates the tracked scene handle once the target scene is ready and returns whether
        /// the external <c>RandomSeedHandshake</c> should be requested on this frame.
        /// </summary>
        public static bool ShouldRequestExternalHandshake(
            bool leanS0ModeEnabled,
            bool ready,
            int sceneHandle,
            string sceneName,
            string seededTargetSceneName,
            ref int randomSeedSceneHandle,
            ref string randomSeedSceneName,
            ref bool randomSeedHandshakePending,
            ref bool leanS0RandomSeededForTargetScene)
        {
            if (leanS0RandomSeededForTargetScene)
            {
                bool arrivedAtTarget = !string.IsNullOrEmpty(seededTargetSceneName)
                    && string.Equals(sceneName, seededTargetSceneName, StringComparison.Ordinal)
                    && sceneHandle != randomSeedSceneHandle;
                if (arrivedAtTarget)
                {
                    randomSeedSceneHandle = sceneHandle;
                    randomSeedSceneName = sceneName;
                    randomSeedHandshakePending = false;
                    leanS0RandomSeededForTargetScene = false;
                }
                else if (ready)
                {
                    if (string.IsNullOrEmpty(seededTargetSceneName) && sceneHandle != randomSeedSceneHandle)
                    {
                        randomSeedSceneHandle = sceneHandle;
                        randomSeedSceneName = sceneName;
                        randomSeedHandshakePending = false;
                    }

                    leanS0RandomSeededForTargetScene = false;
                }
            }

            return !leanS0ModeEnabled
                && !leanS0RandomSeededForTargetScene
                && ready
                && sceneHandle != randomSeedSceneHandle;
        }
    }
}
