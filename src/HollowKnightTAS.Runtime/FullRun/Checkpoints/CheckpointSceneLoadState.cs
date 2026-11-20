using System;
using System.Collections.Generic;

namespace HollowKnightTAS.Runtime.FullRun
{
    [Serializable]
    internal sealed class CheckpointSceneLoadState
    {
        public bool TransitionIteratorFinished;
        public string IteratorType = "", ClosureType = "";
        public Dictionary<string, string> LoadFields = null!, IteratorFields = null!, ClosureFields = null!;
        public Dictionary<string, string>[] Phases = null!;
    }
}
