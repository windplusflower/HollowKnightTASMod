import argparse
import hashlib
import mmap
import struct
import uuid
from pathlib import Path

parser = argparse.ArgumentParser(description="Generate native texture roots from the pinned Unity player and matching PDB.")
parser.add_argument("--pdb", required=True)
parser.add_argument("--engine", required=True)
parser.add_argument("--output", default="src/HollowKnightTAS.Runtime/FullRun/ResidentBuiltinTextureRoots.Layout.cs")
args = parser.parse_args()
pe = Path(args.engine).read_bytes()
if hashlib.sha256(pe).hexdigest() != "d97e92a7640b10580b4e60139eacf01828f74baaef53f75e08b9fdd6193fbe5e":
    raise ValueError("Unity player hash differs")
peoff = struct.unpack_from("<I", pe, 60)[0]
assert pe[peoff:peoff+4] == b"PE\0\0"
section_count = struct.unpack_from("<H", pe, peoff+6)[0]
opt_size = struct.unpack_from("<H", pe, peoff+20)[0]
section_start = peoff+24+opt_size
section_rvas = [struct.unpack_from("<I",pe,section_start+i*40+12)[0] for i in range(section_count)]
u32 = lambda data, offset: struct.unpack_from("<I", data, offset)[0]

with Path(args.pdb).open("rb") as file:
    with mmap.mmap(file.fileno(), 0, access=mmap.ACCESS_READ) as data:
        page, directory_size, blockmap = u32(data, 32), u32(data, 44), u32(data, 52)
        blocks = struct.unpack_from("<" + "I" * ((directory_size + page - 1) // page), data, blockmap * page)
        directory = b"".join(data[block * page:(block + 1) * page] for block in blocks)[:directory_size]
        count = u32(directory, 0)
        sizes = struct.unpack_from("<" + "I" * count, directory, 4)
        position = 4 + count * 4
        streams = []
        for size in sizes:
            length = (size + page - 1) // page if size != 0xffffffff else 0
            streams.append((size, struct.unpack_from("<" + "I" * length, directory, position)))
            position += length * 4

        def stream(index):
            size, blocks = streams[index]
            return b"".join(data[block * page:(block + 1) * page] for block in blocks)[:size]

        info = stream(1)
        assert str(uuid.UUID(bytes_le=info[12:28])) == "0334be90-63dd-4644-bee1-fc8d56c840c2"
        assert u32(info, 8) == 1
        tpi = stream(2)
        offset, index, end = u32(tpi, 4), u32(tpi, 8), u32(tpi, 12)
        records = {}
        while offset + 4 <= len(tpi) and index < end:
            size, kind = struct.unpack_from("<HH", tpi, offset)
            records[index] = kind, tpi[offset + 4:offset + 2 + size]
            offset += size + 2
            index += 1

        def number(value, offset):
            kind = struct.unpack_from("<H", value, offset)[0]
            if kind < 0x8000:
                return kind, offset + 2
            format = {0x8000: "b", 0x8001: "h", 0x8002: "H", 0x8003: "i", 0x8004: "I",
                      0x8009: "q", 0x800a: "Q"}[kind]
            return struct.unpack_from("<" + format, value, offset + 2)[0], offset + 2 + struct.calcsize(format)

        def text(value, offset):
            end = value.index(0, offset)
            return value[offset:end].decode(errors="replace"), end + 1

        def typename(index):
            if index not in records:
                return hex(index)
            kind, value = records[index]
            if kind == 0x1507:
                return text(value, 12)[0]
            if kind in (0x1504, 0x1505):
                _, offset = number(value, 16)
                return text(value, offset)[0]
            if kind == 0x1002:
                return typename(u32(value, 0)) + "*"
            if kind == 0x1503:
                size, _ = number(value, 8)
                return typename(u32(value, 0)) + "[bytes=" + str(size) + "]"
            if kind == 0x1205:
                return typename(u32(value, 0)) + ":bits=" + str(value[4]) + ":position=" + str(value[5])
            return hex(index) + ":kind=" + hex(kind)

        def fields(index):
            kind, value = records[index]
            assert kind == 0x1203
            offset = 0
            rows = []
            while offset + 2 <= len(value):
                if value[offset] >= 0xf0:
                    offset += value[offset] & 0xf
                    continue
                kind = struct.unpack_from("<H", value, offset)[0]
                offset += 2
                if kind == 0x150d:
                    _, type = struct.unpack_from("<HI", value, offset)
                    offset += 6
                    address, offset = number(value, offset)
                    name, offset = text(value, offset)
                    rows.append(dict(name=name, offset=address, type=hex(type), typename=typename(type)))
                elif kind == 0x1502:
                    attributes = struct.unpack_from('<H', value, offset)[0]
                    offset += 2
                    constant, offset = number(value, offset)
                    name, offset = text(value, offset)
                    rows.append(dict(name=name, constant=constant))
                elif kind == 0x1400:
                    _, base = struct.unpack_from("<HI", value, offset)
                    offset += 6
                    address, offset = number(value, offset)
                    base_kind, base_value = records[base]
                    assert base_kind in (0x1504, 0x1505)
                    base_fields = u32(base_value, 4)
                    if not base_fields:
                        base_name = typename(base)
                        for candidate, (candidate_kind, candidate_value) in records.items():
                            if candidate_kind in (0x1504, 0x1505) and u32(candidate_value, 4) and typename(candidate) == base_name:
                                base_fields = u32(candidate_value, 4)
                                break
                    if base_fields:
                        for field in fields(base_fields):
                            field = dict(field)
                            if "offset" in field:
                                field["offset"] += address
                            field["inherited"] = typename(base)
                            rows.append(field)
                    else:
                        rows.append(dict(unsupported="base-fields-unavailable", base=typename(base)))
                elif kind in (0x150f, 0x1510, 0x150e):
                    offset += 6
                    _, offset = text(value, offset)
                elif kind == 0x1511:
                    attributes, _ = struct.unpack_from("<HI", value, offset)
                    offset += 6
                    if (attributes >> 2) & 7 in (4, 6):
                        offset += 4
                    _, offset = text(value, offset)
                elif kind == 0x1409:
                    offset += 6
                elif kind == 0x1404:
                    offset += 2
                    rows.extend(fields(u32(value, offset)))
                    offset += 4
                else:
                    rows.append(dict(unsupported=hex(kind), remaining=len(value) - offset))
                    break
            return rows

        dbi=stream(3); at=64; end_modules=64+u32(dbi,24); sources=[]
        while at<end_modules:
            si=struct.unpack_from('<H',dbi,at+34)[0]; length=u32(dbi,at+36)
            e=dbi.index(0,at+64); e2=dbi.index(0,e+1)
            if si!=65535:sources.append((si,length,4))
            at=(e2+4)&~3
        global_index=struct.unpack_from('<H',dbi,20)[0];sources.append((global_index,streams[global_index][0],0))
        symbols=[]
        for si,length,at in sources:
            raw=stream(si)[:length]
            while at+4<=len(raw):
                length,kind=struct.unpack_from('<HH',raw,at);v=raw[at+4:at+2+length]
                if length<2:break
                if kind in (0x110c,0x110d) and len(v)>10:
                    segment=struct.unpack_from('<H',v,8)[0]
                    if 0<segment<=len(section_rvas):symbols.append(dict(type=u32(v,0),rva=section_rvas[segment-1]+u32(v,4),name=v[10:].split(b'\0')[0].decode()))
                at+=length+2
        bases={}; definitions={}
        for ti,(ki,v) in records.items():
            if ki in (0x1504,0x1505) and u32(v,4):
                name=typename(ti);definitions[name]=ti;raw=records[u32(v,4)][1]
                if len(raw)>=8 and struct.unpack_from('<H',raw,0)[0]==0x1400:bases[name]=typename(u32(raw,4))
        def texture(name,active=()):
            return name=='Texture' or name not in active and name in bases and texture(bases[name],active+(name,))
        def rootdesc(ti):
            if ti not in records:return None
            ki,v=records[ti]
            if ki==0x1001:return rootdesc(u32(v,0))
            if ki==0x1002 and texture(typename(u32(v,0))):return (typename(u32(v,0)),1)
            if ki==0x1503:
                child=rootdesc(u32(v,0));size,_=number(v,8)
                if child and child[1]==1 and size%8==0:return (child[0],size//8)
            return None
        unique={}
        for symbol in symbols:
            desc=rootdesc(symbol['type'])
            if desc:unique[(symbol['rva'],symbol['name'])]=(symbol,desc)
        byname={s['name']:s for s in symbols}
        rows=[]
        for (rva,name),(symbol,(declared,count)) in sorted(unique.items()):
            rtti=byname['TypeContainer<'+declared+'>::rtti']['rva']
            rows.append('            new Root(0x%x, %d, 0x%x, "%s"),'%(rva,count,rtti,name))
        owner=byname['gScreenManager'];assert typename(owner['type'])=='ScreenManagerWin*'
        owner_fields=fields(u32(records[definitions['ScreenManagerWin']][1],4));blit=next(f for f in owner_fields if f.get('name')=='m_BlitTexture');assert blit['typename']=='LowerResBlitTexture*'
        vt=byname["ScreenManagerWin::`vftable'"]['rva'];rtti=byname['TypeContainer<LowerResBlitTexture>::rtti']['rva']
        output='// Generated by scripts/Generate-ResidentBuiltinTextureRoots.py; do not edit layout rows.\nnamespace HollowKnightTAS.Runtime.FullRun\n{\n    internal static partial class ResidentBuiltinTextureRoots\n    {\n        internal const string EngineSha256 = "D97E92A7640B10580B4E60139EACF01828F74BAAEF53F75E08B9FDD6193FBE5E";\n        internal const string PdbIdentity = "0334be90-63dd-4644-bee1-fc8d56c840c2/1";\n        private static readonly Root[] Layout = new[] {\n'+ '\n'.join(rows) + '\n        };\n        private static readonly OwnerRoot ScreenBlit = new OwnerRoot(0x%x, 0x%x, %d, 0x%x);\n    }\n}\n'%(owner['rva'],vt,blit['offset'],rtti)
        Path(args.output).write_text(output,encoding='utf-8')
        print('Generated',len(rows),'typed static root descriptors plus ScreenManager field owner:',args.output)
