import struct, json, base64

packet_path = "artifacts/scene-checkpoints/p3b/cached-b8-source/packet.bin"
with open(packet_path, "rb") as f:
    header = f.read(64)
    magic, version, meta_len, domain_count = struct.unpack("<IIII", header[:16])
    domain_lens = struct.unpack("<12I", header[16:64])
    meta_bytes = f.read(meta_len)
    
    domains = []
    for dlen in domain_lens:
        domains.append(f.read(dlen))

graph_bytes = domains[3]
# First 4 bytes magic, next 4 version, next 4 string count
magic, ver, string_count = struct.unpack("<III", graph_bytes[:12])
print(f"Magic: {hex(magic)}, Version: {ver}, String count: {string_count}")

offset = 12
delegates = []
for i in range(string_count):
    slen, = struct.unpack("<I", graph_bytes[offset:offset+4])
    offset += 4
    sbytes = graph_bytes[offset:offset+slen]
    offset += slen
    try:
        stext = sbytes.decode('utf-8')
        # check if it is base64 of a delegate descriptor
        try:
            raw = base64.b64decode(stext)
            obj = json.loads(raw)
            if isinstance(obj, dict) and "Calls" in obj:
                delegates.append((i, stext, obj))
        except:
            pass
    except:
        pass

print(f"Found {len(delegates)} delegate strings:")
for idx, text, d in delegates:
    print(f"Delegate [{idx}]: {json.dumps(d, indent=2)}")
