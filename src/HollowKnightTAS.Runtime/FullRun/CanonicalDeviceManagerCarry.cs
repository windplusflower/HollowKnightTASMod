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
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(Manager), typeof(Manager[]), typeof(uint[]), typeof(byte[]) };
        internal sealed class Data { public int Version = 2; public Manager[] Managers = Array.Empty<Manager>(); }
        internal sealed class Manager
        {
            public int Order, RegisteredDevices, AttachedDevices, DetachedDevices, SystemProfiles, CustomProfiles;
            public string ModuleMvid = "", Type = "";
            public uint[] NativeEvents = Array.Empty<uint>();
            public string SystemProfileContract = "";
        }
        internal static Data Copy(Data data)
        {
            if (data == null || data.Version != 2 || data.Managers == null || data.Managers.Length > 1)
                throw new InvalidDataException("SDK manager registration is outside the bounded original native owner contract.");
            foreach (var item in data.Managers)
                if (item == null || item.Order != 0 || item.ModuleMvid != CanonicalSceneMappingCarry.ModuleMvid || item.Type != "InControl.NativeInputDeviceManager"
                    || item.RegisteredDevices != 0 || item.AttachedDevices < 0 || item.AttachedDevices > 16 || item.DetachedDevices != 0 || item.SystemProfiles < 0 || item.SystemProfiles > 4096
                    || item.CustomProfiles != 0 || item.NativeEvents == null || item.NativeEvents.Length > 4096
                    || item.SystemProfileContract == null || item.SystemProfileContract.Length < 12 || item.SystemProfileContract.Length > 1398104)
                    throw new InvalidDataException("SDK manager owner, devices, profile configuration or managed event buffer is unregistered.");
            foreach (var item in data.Managers) {
                byte[] packet;
                try { packet = Convert.FromBase64String(item.SystemProfileContract); }
                catch (FormatException error) { throw new InvalidDataException("SDK manager configuration packet encoding differs.", error); }
                if (packet.Length < 8 || packet.Length > 1024 * 1024 || Convert.ToBase64String(packet) != item.SystemProfileContract)
                    throw new InvalidDataException("SDK manager configuration packet is outside its canonical byte contract.");
            }
            return new Data { Version = data.Version, Managers = data.Managers.Select(item => new Manager {
                Order = item.Order, ModuleMvid = item.ModuleMvid, Type = item.Type, RegisteredDevices = item.RegisteredDevices,
                AttachedDevices = item.AttachedDevices, DetachedDevices = item.DetachedDevices, SystemProfiles = item.SystemProfiles,
                CustomProfiles = item.CustomProfiles, NativeEvents = (uint[])item.NativeEvents.Clone(),
                SystemProfileContract = item.SystemProfileContract }).ToArray() };
        }
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var root = view.Root(Root);
            return Copy(new Data { Version = (int)view.Field(root, typeof(Data), nameof(Data.Version))!,
                Managers = (Manager[])view.Field(root, typeof(Data), nameof(Data.Managers))! });
        }
        internal static void RequireSameConfiguration(Data source, Data setup)
        {
            var saved = Copy(source); var current = Copy(setup);
            if (saved.Managers.Length != current.Managers.Length || saved.Managers.Where((item, index) => {
                var owner = current.Managers[index];
                return item.Type != owner.Type || item.ModuleMvid != owner.ModuleMvid || item.SystemProfiles != owner.SystemProfiles
                    || item.NativeEvents.Length != owner.NativeEvents.Length
                    || !string.Equals(item.SystemProfileContract, owner.SystemProfileContract, StringComparison.Ordinal);
            }).Any()) throw new InvalidDataException("Saved SDK manager registration/configuration differs.");
        }
    }
}
