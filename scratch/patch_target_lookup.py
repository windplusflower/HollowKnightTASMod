import os

path = r'src/HollowKnightTAS.Runtime/FullRun/ResidentTargetAssetLookup.cs'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Normalize line endings for replacement
crlf = '\r\n' in content
content_norm = content.replace('\r\n', '\n')

decl_target = '''        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetInstanceIdDelegate(IntPtr remapper, ref SerializedIdentifier id);'''

decl_replacement = '''        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetInstanceIdDelegate(IntPtr remapper, ref SerializedIdentifier id);
        [StructLayout(LayoutKind.Sequential)]
        private struct StringRef
        {
            internal IntPtr Data;
            internal ulong Length;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AddPathNameDelegate(IntPtr manager, ref StringRef path, [MarshalAs(UnmanagedType.I1)] bool flag);'''

if decl_target not in content_norm:
    raise Exception('decl_target not found')
content_norm = content_norm.replace(decl_target, decl_replacement)

body_target = '''            RequireCode(engine, 0x9073cf, 143, "a685abf1bb03af7f0cf41beccd538c3a881daf517b74605294276ffcde266b68");
            RequireCode(engine, 0x907390, 63, "e6a35ef4135eb1cc5d50a99ab4a8396e8506f24aae4ec3e87916eae2786d46c0");'''

body_replacement = '''            RequireCode(engine, 0x9073cf, 143, "a685abf1bb03af7f0cf41beccd538c3a881daf517b74605294276ffcde266b68");
            RequireCode(engine, 0x907390, 63, "e6a35ef4135eb1cc5d50a99ab4a8396e8506f24aae4ec3e87916eae2786d46c0");
            RequireCode(engine, 0x902650, 48, "3b5e732a3520f4a8466036a660cdb5da57c58a595a4484cc7dc10116f4efebaa");'''

if body_target not in content_norm:
    raise Exception('body_target not found')
content_norm = content_norm.replace(body_target, body_replacement)

logic_target = '''                bool generated = false;
                if (ids.Count == 0 && matchedIndices.Count == 1) {
                    var getOrGen = (GetInstanceIdDelegate)Marshal.GetDelegateForFunctionPointer(
                        IntPtr.Add(engine, 0x907390), typeof(GetInstanceIdDelegate));
                    var identifier = new SerializedIdentifier { File = matchedIndices[0], Object = local };
                    var genId = getOrGen(remapper, ref identifier);
                    if (genId != 0) { ids.Add(genId); generated = true; }
                }
                if (!paths.SequenceEqual(M.Read(IntPtr.Add(manager, 352), 32))
                    || (!generated && !remap.SequenceEqual(M.Read(remapper, remap.Length))))
                    throw Error("persistent source directory changed during lookup");'''

logic_replacement = '''                bool registeredPath = false;
                if (matchedIndices.Count == 0) {
                    var addPath = (AddPathNameDelegate)Marshal.GetDelegateForFunctionPointer(
                        IntPtr.Add(engine, 0x902650), typeof(AddPathNameDelegate));
                    var rawBytes = Encoding.UTF8.GetBytes(path + "\\0");
                    var pinned = GCHandle.Alloc(rawBytes, GCHandleType.Pinned);
                    try {
                        var stringRef = new StringRef { Data = pinned.AddrOfPinnedObject(), Length = (ulong)(rawBytes.Length - 1) };
                        var fileIndex = addPath(manager, ref stringRef, true);
                        if (fileIndex < 0) throw Error($"failed to register persistent pathname: {path}");
                        matchedIndices.Add(fileIndex);
                        registeredPath = true;
                    } finally {
                        pinned.Free();
                    }
                }
                bool generated = false;
                if (ids.Count == 0 && matchedIndices.Count == 1) {
                    var getOrGen = (GetInstanceIdDelegate)Marshal.GetDelegateForFunctionPointer(
                        IntPtr.Add(engine, 0x907390), typeof(GetInstanceIdDelegate));
                    var identifier = new SerializedIdentifier { File = matchedIndices[0], Object = local };
                    var genId = getOrGen(remapper, ref identifier);
                    if (genId != 0) { ids.Add(genId); generated = true; }
                }
                if (!registeredPath && !paths.SequenceEqual(M.Read(IntPtr.Add(manager, 352), 32)))
                    throw Error("persistent source directory changed during lookup");
                if (!generated && !remap.SequenceEqual(M.Read(remapper, remap.Length)))
                    throw Error("persistent source remapper changed during lookup");'''

if logic_target not in content_norm:
    raise Exception('logic_target not found')
content_norm = content_norm.replace(logic_target, logic_replacement)

final_content = content_norm.replace('\n', '\r\n') if crlf else content_norm
with open(path, 'w', encoding='utf-8') as f:
    f.write(final_content)

print('Updated ResidentTargetAssetLookup.cs successfully')
