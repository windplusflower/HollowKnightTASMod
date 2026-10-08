namespace HollowKnightTAS.Core.Movie
{
    // Shared with the standalone injector so boot receipts and replay validation
    // cannot advertise different execution rules.
    public static class NativeExecutionProfile
    {
        // v9 selects each frame's duration before Unity's separate TimeUpdate.
        // Earlier recordings ran gameplay with a fixed or one-frame-late delta.
        public const string ProfileId = "hktas-unity-input-playerloop-canonical-cinematics-2026-v9";
    }
}
