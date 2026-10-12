using System;
using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Runtime.Input
{
    [Serializable]
    internal sealed class CheckpointMouseValue
    {
        public int X, Y;
        public short DeltaX, DeltaY, Wheel;
        public uint Buttons;
        internal static CheckpointMouseValue Capture(MouseFrameState value) => new CheckpointMouseValue
        { X = value.XQ16, Y = value.YQ16, DeltaX = value.DeltaXQ15, DeltaY = value.DeltaYQ15, Wheel = value.WheelQ15, Buttons = value.Buttons };
        internal MouseFrameState Create()
        {
            if ((Buttons & ~511u) != 0) throw new InvalidDataException("Invalid checkpoint mouse buttons.");
            return new MouseFrameState(X, Y, DeltaX, DeltaY, Buttons, Wheel);
        }
    }
    [Serializable]
    internal sealed class CheckpointMouseModule
    {
        public string Identity = "";
        public Dictionary<string, string> Fields = null!;
    }
    [Serializable]
    internal sealed class CheckpointMouseState
    {
        public bool Enabled, Suspended;
        public int Width, Height;
        public uint PreviousInControlButtons, PreviousHkButtons;
        public float PreviousHkX, PreviousHkY;
        public CheckpointMouseValue InControl = null!, Hk = null!;
        public CheckpointMouseModule[] Modules = null!;
    }
}
