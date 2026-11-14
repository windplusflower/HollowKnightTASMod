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
delegates = []
for i in range(string_count):
    slen, = struct.unpack("<I", graph_bytes[offset:offset+4])
    offset += 4
    sbytes = graph_bytes[offset:offset+slen]
    offset += slen
    try:
        stext = sbytes.decode('utf-8')
        raw = base64.b64decode(stext)
        obj = json.loads(raw)
        if isinstance(obj, dict) and "Calls" in obj:
            delegates.append((i, stext, obj))
    except:
        pass

by_asm = {}
for idx, text, d in delegates:
    for call in d["Calls"]:
        asm = call["DeclaringType"]["Assembly"]
        by_asm.setdefault(asm, []).append((call["DeclaringType"]["Name"], call["Token"]))

for asm, items in by_asm.items():
    print(f"Assembly: {asm} (count: {len(items)})")
    if asm.startswith("HollowKnightTAS"):
        for name, tok in items:
            print(f"  {name} : token={hex(tok)} ({tok})")
