"""Disassemble readers from the pinned local engine; never modify the running game."""
import argparse
import hashlib
import json
import mmap
from pathlib import Path
import struct
import uuid

import capstone
import pefile


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("symbols", type=Path)
parser.add_argument("--engine", type=Path, default=Path(r"D:\SteamLibrary\steamapps\common\Hollow Knight\UnityPlayer.dll"))
parser.add_argument("--pdb", type=Path)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
binary = args.engine.read_bytes()
digest = hashlib.sha256(binary).hexdigest()
if digest != "d97e92a7640b10580b4e60139eacf01828f74baaef53f75e08b9fdd6193fbe5e":
    raise ValueError("engine differs from the pinned P0 build")
symbols = json.loads(args.symbols.read_text(encoding="utf-8-sig"))
engine = pefile.PE(data=binary)
debug = [binary[item.struct.PointerToRawData:item.struct.PointerToRawData + item.struct.SizeOfData]
         for item in engine.DIRECTORY_ENTRY_DEBUG if item.struct.Type == 2]
if not any(row[:4] == b"RSDS" and str(uuid.UUID(bytes_le=row[4:20])) == symbols["guid"]
           and struct.unpack_from("<I", row, 20)[0] == symbols["age"] for row in debug):
    raise ValueError("symbol GUID/age differs from the engine")
disassembler = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
output = [f"engineSha256={digest} guid={symbols['guid']} age={symbols['age']}"]
extents = {}
if args.pdb:
    # An x64 unwind entry may describe just one prolog/epilog fragment. Use
    # the matching PDB procedure's declared extent when available instead of
    # truncating a reader at its first return or unwind fragment.
    with args.pdb.open("rb") as file, mmap.mmap(file.fileno(), 0, access=mmap.ACCESS_READ) as data:
        u32 = lambda value, position: struct.unpack_from("<I", value, position)[0]
        page, directory_size, block_map = u32(data, 32), u32(data, 44), u32(data, 52)
        blocks = struct.unpack_from("<" + "I" * ((directory_size + page - 1) // page), data, block_map * page)
        directory = b"".join(data[block * page:(block + 1) * page] for block in blocks)[:directory_size]
        stream_count = u32(directory, 0)
        sizes = struct.unpack_from("<" + "I" * stream_count, directory, 4)
        position, streams = 4 + 4 * stream_count, []
        for size in sizes:
            count = (size + page - 1) // page if size != 0xffffffff else 0
            streams.append((size, struct.unpack_from("<" + "I" * count, directory, position)))
            position += 4 * count

        def stream(index):
            size, blocks = streams[index]
            return b"".join(data[block * page:(block + 1) * page] for block in blocks)[:size]

        info = stream(1)
        if str(uuid.UUID(bytes_le=info[12:28])) != symbols["guid"] or u32(info, 8) != symbols["age"]:
            raise ValueError("PDB GUID/age differs from the engine")
        dbi = stream(3)
        position, end = 64, 64 + u32(dbi, 24)
        while position < end:
            module_stream = struct.unpack_from("<H", dbi, position + 34)[0]
            symbol_bytes = u32(dbi, position + 36)
            next_module = dbi.index(0, dbi.index(0, position + 64) + 1) + 1
            position = (next_module + 3) & ~3
            if module_stream == 0xffff or symbol_bytes < 4:
                continue
            records = stream(module_stream)[:symbol_bytes]
            offset = 4
            while offset + 4 <= len(records):
                length, kind = struct.unpack_from("<HH", records, offset)
                if length < 2 or offset + 2 + length > len(records):
                    raise ValueError("invalid PDB module symbol range")
                value = records[offset + 4:offset + 2 + length]
                if kind in (0x110f, 0x1110, 0x1146, 0x1147) and len(value) >= 35:
                    size, address, section = u32(value, 12), u32(value, 28), struct.unpack_from("<H", value, 32)[0]
                    previous = extents.get((section, address))
                    if previous is not None and previous != size:
                        raise ValueError("conflicting PDB procedure extents")
                    extents[(section, address)] = size
                offset += length + 2
count = 0
for symbol in symbols["symbols"]:
    section = engine.sections[symbol["segment"] - 1]
    if not section.Characteristics & 0x20000000:
        continue
    address = section.VirtualAddress + symbol["offset"]
    exact_size = extents.get((symbol["segment"], symbol["offset"]))
    ranges = [entry.struct.EndAddress for entry in engine.DIRECTORY_ENTRY_EXCEPTION
              if entry.struct.BeginAddress <= address < entry.struct.EndAddress]
    if exact_size is not None:
        size = exact_size
    elif not ranges:
        # Leaf functions may have no unwind entry; cap at 256 bytes and their
        # first return instead of reading an unrelated function as evidence.
        size = 256
    else:
        size = min(ranges) - address
    if not 0 < size <= 65536:
        raise ValueError(f"function extent exceeds explicit budget: {symbol['name']}")
    extent_source = "pdb-procedure" if exact_size is not None else "unwind-fragment" if ranges else "bounded-leaf"
    output.append(f"\n{symbol['name']} RVA={address:x} bytes={size} extent={extent_source}")
    for instruction in disassembler.disasm(engine.get_data(address, size), address):
        output.append(f"{instruction.address:x}: {instruction.mnemonic} {instruction.op_str}")
        if exact_size is None and not ranges and instruction.mnemonic.startswith("ret"):
            break
    count += 1
args.output.write_text("\n".join(output) + "\n", encoding="utf-8")
print(json.dumps({"functions": count, "output": str(args.output), "engineSha256": digest}))
