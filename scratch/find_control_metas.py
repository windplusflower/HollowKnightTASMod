import struct, json, base64

packet_path = "artifacts/scene-checkpoints/p3b/cached-b8-source/packet.bin"
with open(packet_path, "rb") as f:
    header = f.read(64)
    magic, version, meta_len, domain_count = struct.unpack("<IIII", header[:16])
    domain_lens = struct.unpack("<12I", header[16:64])
    meta_bytes = f.read(meta_len)
    domains = [f.read(dlen) for dlen in domain_lens]

graph_bytes = domains[3]
magic, ver, string_count = struct.unpack("<III", graph_bytes[:12])

offset = 12
meta_prefix = "resident-metadata/1/"
control_plane = ["HollowKnightTAS", "HollowKnightTAS.Core", "HollowKnightTAS.ClockPayload"]
control_metas = []
for i in range(string_count):
    slen, = struct.unpack("<I", graph_bytes[offset:offset+4])
    offset += 4
    sbytes = graph_bytes[offset:offset+slen]
    offset += slen
    try:
        stext = sbytes.decode('utf-8')
        if stext.startswith(meta_prefix):
            b64 = stext[len(meta_prefix):]
            raw = base64.b64decode(b64)
            obj = json.loads(raw)
            asm = obj.get("Assembly") or (obj.get("Type") and obj["Type"].get("Assembly"))
            if any(asm == cp or (asm and asm.startswith("MonoMod")) for cp in control_plane):
                control_metas.append((i, stext, obj))
    except:
        pass

print(f"Found {len(control_metas)} control plane metadata strings:")
for idx, text, obj in control_metas:
    print(f"Meta [{idx}]: {json.dumps(obj, indent=2)}")
