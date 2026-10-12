using System;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Managed state of the actual SDK owner. Native event discovery/threads
    // remain a separate gate; no native handle or Type.GetHashCode is saved.
    internal static class CanonicalDeviceManagerCarry
    {
        internal const string Root = "R6/InputManager-device-manager-registration";
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(Manager), typeof(Manager[]), typeof(uint[]) };
        internal sealed class Data { public int Version = 1; public Manager[] Managers = Array.Empty<Manager>(); }
        internal sealed class Manager
        {
            public int Order, RegisteredDevices, AttachedDevices, DetachedDevices, SystemProfiles, CustomProfiles;
            public string ModuleMvid = "", Type = "";
            public uint[] NativeEvents = Array.Empty<uint>();
        }
        internal static Data Copy(Data data)
        {
            if (data == null || data.Version != 1 || data.Managers == null || data.Managers.Length > 1)
                throw new InvalidDataException("SDK manager registration is outside the bounded original native owner contract.");
            foreach (var item in data.Managers)
                if (item == null || item.Order != 0 || item.ModuleMvid != CanonicalSceneMappingCarry.ModuleMvid || item.Type != "InControl.NativeInputDeviceManager"
                    || item.RegisteredDevices != 0 || item.AttachedDevices != 0 || item.DetachedDevices != 0 || item.SystemProfiles < 0 || item.SystemProfiles > 4096
                    || item.CustomProfiles != 0 || item.NativeEvents == null || item.NativeEvents.Length > 4096)
                    throw new InvalidDataException("SDK manager owner, devices, profile configuration or managed event buffer is unregistered.");
            return new Data { Version = data.Version, Managers = data.Managers.Select(item => new Manager {
                Order = item.Order, ModuleMvid = item.ModuleMvid, Type = item.Type, RegisteredDevices = item.RegisteredDevices,
                AttachedDevices = item.AttachedDevices, DetachedDevices = item.DetachedDevices, SystemProfiles = item.SystemProfiles,
                CustomProfiles = item.CustomProfiles, NativeEvents = (uint[])item.NativeEvents.Clone() }).ToArray() };
        }
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var root = view.Root(Root);
            return Copy(new Data { Version = (int)view.Field(root, typeof(Data), nameof(Data.Version))!,
                Managers = (Manager[])view.Field(root, typeof(Data), nameof(Data.Managers))! });
        }
    }
}
