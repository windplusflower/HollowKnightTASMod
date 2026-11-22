using System.Reflection;
using HollowKnightTAS.Core.Inspector;

namespace HollowKnightTAS.Runtime.Observation
{
    /// <summary>Read-only Unity Random.state observer backed by InfoRngTracker.</summary>
    internal sealed class RuntimeInfoRng
    {
        private static readonly FieldInfo? S0Field = ResolveIntField("s0");
        private static readonly FieldInfo? S1Field = ResolveIntField("s1");
        private static readonly FieldInfo? S2Field = ResolveIntField("s2");
        private static readonly FieldInfo? S3Field = ResolveIntField("s3");

        private readonly InfoRngTracker tracker = new InfoRngTracker();

        public bool HasValue => tracker.HasValue;
        public long? RollTimes => tracker.HasValue ? tracker.RollTimes : (long?)null;
        public int? LastDelta => tracker.HasValue ? tracker.LastDelta : (int?)null;
        public string? Formatted => tracker.Formatted;

        public void EnsureBaseline()
        {
            if (!tracker.HasValue && TryReadCurrentState(out uint s0, out uint s1, out uint s2, out uint s3))
                tracker.SyncBaseline(s0, s1, s2, s3);
        }

        public void SyncBaseline()
        {
            if (TryReadCurrentState(out uint s0, out uint s1, out uint s2, out uint s3))
                tracker.SyncBaseline(s0, s1, s2, s3);
        }

        public void CompleteFrame()
        {
            if (TryReadCurrentState(out uint s0, out uint s1, out uint s2, out uint s3))
                tracker.Observe(s0, s1, s2, s3);
        }

        public static bool TryReadCurrentState(out uint s0, out uint s1, out uint s2, out uint s3)
        {
            if (S0Field == null || S1Field == null || S2Field == null || S3Field == null)
            {
                s0 = s1 = s2 = s3 = 0;
                return false;
            }

            // Read a value copy of Random.state via reflection; never call Random.value or write Random.state.
            object boxed = UnityEngine.Random.state;
            s0 = unchecked((uint)(int)S0Field.GetValue(boxed));
            s1 = unchecked((uint)(int)S1Field.GetValue(boxed));
            s2 = unchecked((uint)(int)S2Field.GetValue(boxed));
            s3 = unchecked((uint)(int)S3Field.GetValue(boxed));
            return true;
        }

        private static FieldInfo? ResolveIntField(string name)
        {
            var field = typeof(UnityEngine.Random.State).GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null && field.FieldType == typeof(int) && !field.IsStatic ? field : null;
        }
    }
}
