using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Source boundary identity is independent of the target's local parked B.
    internal sealed class CanonicalActualDRoute
    {
        internal static int SelectOrdinal(string? text, bool enabled)
        {
            if (text == null) return 1;
            if (!enabled || !int.TryParse(text, out var value) || value < 1 || value > 64)
                throw new InvalidDataException("Invalid experimental source boundary ordinal.");
            return value;
        }
        public int Ordinal;
        public long MovieFrame;
        public string SourceScene = "", TargetScene = "", ContinuationSha256 = "";
        public SortedDictionary<string, string> LoadFields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        private static readonly string[] Names = { "AlwaysUnloadUnusedAssets", "EntryDelay", "EntryGateName", "HeroLeaveDirection",
            "IsFirstLevelForPlayer", "PreventCameraFadeOut", "SceneName", "Visualization", "WaitForSceneTransitionCameraFade", "forceWaitFetch" };
        internal void Validate(int selectedOrdinal, long movie)
        {
            if (Ordinal != selectedOrdinal || Ordinal < 1 || Ordinal > 64 || MovieFrame != movie || movie < 0
                || string.IsNullOrEmpty(SourceScene) || string.IsNullOrEmpty(TargetScene) || SourceScene.Length > 256 || TargetScene.Length > 256
                || SourceScene.IndexOf('\0') >= 0 || TargetScene.IndexOf('\0') >= 0
                || ContinuationSha256 == null || ContinuationSha256.Length != 64 || ContinuationSha256.Any(c => !Uri.IsHexDigit(c))
                || LoadFields == null || !LoadFields.Keys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(Names)
                || LoadFields.Values.Any(value => value == null || value.Length > 2048))
                throw new InvalidDataException("Actual D selected source boundary identity differs.");
        }
        internal void RequireSame(CanonicalActualDRoute actual, int selectedOrdinal, long movie)
        {
            Validate(selectedOrdinal, movie); actual.Validate(selectedOrdinal, movie);
            if (SourceScene != actual.SourceScene || TargetScene != actual.TargetScene || ContinuationSha256 != actual.ContinuationSha256
                || !LoadFields.SequenceEqual(actual.LoadFields))
                throw new InvalidDataException("Actual D source route and original continuation fields disagree.");
        }
    }
}
