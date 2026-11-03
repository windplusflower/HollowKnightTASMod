"""Generate two pinned input rows; reuse Director's linker thunk ABI unchanged."""
import argparse
import hashlib
from pathlib import Path
import capstone
import pefile
from capstone.x86 import (X86_OP_MEM, X86_OP_IMM, X86_REG_RIP,
                          X86_REG_R11, X86_REG_R11D, X86_REG_R11W, X86_REG_R11B)

parser = argparse.ArgumentParser()
parser.add_argument('--player', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()
raw = Path(args.player).read_bytes()
assert hashlib.sha256(raw).hexdigest() == 'd97e92a7640b10580b4e60139eacf01828f74baaef53f75e08b9fdd6193fbe5e'
pe = pefile.PE(data=raw)
functions = {r.struct.BeginAddress: r.struct for r in pe.DIRECTORY_ENTRY_EXCEPTION}
cs = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
cs.detail = True
rows = []
for name, abi, rva, length in [('QueueInputEvent', 'BP', 0x292a30, 574),
                                ('SendInputEventsToScript', 'VI', 0x293690, 518)]:
    body = pe.get_data(rva, length)
    fnv = 14695981039346656037
    for byte in body:
        fnv = ((fnv ^ byte) * 1099511628211) & 0xffffffffffffffff
    cut = 0
    for ins in cs.disasm(body, rva):
        assert not any(o.type == X86_OP_MEM and o.mem.base == X86_REG_RIP for o in ins.operands)
        assert ins.mnemonic not in ('call', 'jmp') and not ins.mnemonic.startswith('j')
        cut += ins.size
        if cut >= 5:
            break
    assert cut == 5
    # The shared linker continuation uses volatile R11. Check every reachable
    # path until an original write, call (ABI clobber), or return kills it.
    decoded = {ins.address: ins for ins in cs.disasm(body, rva)}
    pending, visited = [rva + cut], set()
    while pending:
        pc = pending.pop()
        if pc in visited:
            continue
        visited.add(pc)
        assert pc in decoded, (name, hex(pc), 'unresolved continuation')
        ins = decoded[pc]
        reads, writes = ins.regs_access()
        assert not set(reads) & {X86_REG_R11, X86_REG_R11D, X86_REG_R11W, X86_REG_R11B}
        if set(writes) & {X86_REG_R11, X86_REG_R11D} or ins.group(capstone.CS_GRP_CALL) or ins.group(capstone.CS_GRP_RET):
            continue
        if ins.group(capstone.CS_GRP_JUMP):
            assert ins.operands[0].type == X86_OP_IMM
            pending.append(ins.operands[0].imm)
        if ins.mnemonic != 'jmp':
            pending.append(ins.address + ins.size)
    entry = functions[rva]
    if name == 'SendInputEventsToScript':
        assert sum(rva <= start < rva + length for start in functions) == 5
    header = pe.get_data(entry.UnwindData, 4)
    assert header[0] == 1
    codes = pe.get_data(entry.UnwindData + 4, header[2] * 2)
    selected = b''
    frame = 0
    at = 0
    while at < len(codes):
        off, desc = codes[at:at + 2]
        op, info = desc & 15, desc >> 4
        slots = {0: 1, 1: 2 if info == 0 else 3, 2: 1, 3: 1,
                 4: 2, 5: 3, 8: 2, 9: 3, 10: 1}[op]
        size = slots * 2
        if off <= cut:
            selected += codes[at:at + size]
            if op == 3:
                frame = header[3]
        at += size
    assert at == len(codes)
    # Matches resident_director_entry_hooks.h's R11 + JMP [R11+0] sequence.
    unwind = bytes([1, cut + 11, len(selected) // 2, frame]) + selected
    byte_list = lambda b: ','.join(str(x) for x in b)
    rows.append(f' X({name}, {abi}, 0x{rva:x}u, {length}u, UINT64_C(0x{fnv:016x}), '
                f'{cut}u, "{byte_list(body[:cut])}", "{byte_list(unwind)}")')
text = ['#ifndef HKTAS_RESIDENT_INPUT_ENTRY_LAYOUT_H', '#define HKTAS_RESIDENT_INPUT_ENTRY_LAYOUT_H',
        '/* Fixed PE d97e92a7; PDB 0334be90-63dd-4644-bee1-fc8d56c840c2/1.',
        ' * Layout only: no runtime installation or platform-input qualification. */',
        '#define HKTAS_INPUT_ENTRIES(X) ' + chr(92)]
text += [row + (' ' + chr(92) if i + 1 < len(rows) else '') for i, row in enumerate(rows)]
text += ['#endif', '']
Path(args.output).write_text('\n'.join(text))
print('Generated 2 pinned input rows:', args.output)
