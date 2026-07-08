"""
gem5 O3CPU SE mode config for Horologium comparison.

Runs a Linux-ABI RISC-V binary through gem5's out-of-order CPU (RiscvO3CPU)
in syscall-emulation mode, with structural parameters matched to Horologium's
OooeTrain "+Matched" calibration config (w2 by default).

Usage:
  gem5 gem5-scripts/o3cpu_riscv.py --cmd <elf> [options]

The stats file lands at m5out/stats.txt.  Key fields to extract:
  system.cpu.ipc              — instructions per cycle (IPC)
  system.cpu.numCycles        — total simulated cycles
  system.cpu.committedInsts   — total committed instructions

Horologium reference (+Matched w2):
  ROB=30, IQ=8 per class (5 classes), LQ/SQ=32, issue/commit width=2,
  LoadHitLatency=4, BypassLatency=1, DivLatency=23, RAS=16, L1 16KB split.
"""

import argparse
import sys

import m5
from m5.objects import (
    AddrRange,
    Root,
    SimpleMemory,
    SrcClockDomain,
    System,
    SystemXBar,
    VoltageDomain,
    Cache,
    L2XBar,
    Process,
    SEWorkload,
    DefaultFUPool,
    FUPool,
    FUDesc,
    OpDesc,
)

# ── Argument parsing ──────────────────────────────────────────────────────────

parser = argparse.ArgumentParser(
    description="gem5 O3CPU SE mode for Horologium IPC comparison."
)
parser.add_argument("--cmd",         required=True,
                    help="Path to the Linux-ABI RV32 ELF to simulate")
parser.add_argument("--args",        default="",
                    help="Command-line arguments to pass to the program")
parser.add_argument("--cpu-clock",   default="1GHz")
parser.add_argument("--sys-clock",   default="1GHz")
parser.add_argument("--mem-size",    default="512MB")

# Pipeline width (all stages set to same value for simplicity)
parser.add_argument("--width",       type=int, default=2,
                    help="Fetch/decode/rename/dispatch/issue/commit width (default: 2)")

# ROB / queue sizes (defaults match Horologium +Matched)
parser.add_argument("--rob",         type=int, default=30,
                    help="ROB entries (default: 30, matches Olympia retire_queue_depth)")
parser.add_argument("--iq",          type=int, default=8,
                    help="Issue queue entries per IQUnit (default: 8, matches Olympia scheduler_size)")
parser.add_argument("--lq",          type=int, default=32,
                    help="Load queue entries (default: 32)")
parser.add_argument("--sq",          type=int, default=32,
                    help="Store queue entries (default: 32)")

# Physical register file (must be > arch regs + ROB; typical: ROB + 32 extra)
parser.add_argument("--phys-int",    type=int, default=64,
                    help="Physical int registers (default: 64)")
parser.add_argument("--phys-fp",     type=int, default=64,
                    help="Physical FP registers (default: 64)")

# Cache
parser.add_argument("--l1i-size",    default="16kB")
parser.add_argument("--l1d-size",    default="16kB")
parser.add_argument("--l2cache",     action="store_true")
parser.add_argument("--l2-size",     default="256kB")

# Functional unit latencies (default: gem5 DefaultFUPool values)
parser.add_argument("--div-lat",     type=int, default=None,
                    help="IntDiv latency in cycles (default: 20, gem5 DefaultFUPool). "
                         "Set to 23 to match Horologium DivLatency=23.")
parser.add_argument("--mem-lat-ns",  default=None,
                    help="SimpleMemory latency (default: '30ns'). "
                         "Set to '10ns' to match Horologium HtifMemory ~10-cycle miss.")

args = parser.parse_args()

# ── FU pool helper ────────────────────────────────────────────────────────────

def make_fu_pool(div_lat=None):
    """Return DefaultFUPool, optionally patching IntDiv latency."""
    default = DefaultFUPool()
    if div_lat is None:
        return default
    new_fu_list = []
    for fu in default.FUList:
        if any(str(op.opClass) == 'IntDiv' for op in fu.opList):
            new_ops = [
                OpDesc(opClass='IntDiv', opLat=div_lat, pipelined=False)
                if str(op.opClass) == 'IntDiv'
                else OpDesc(opClass=str(op.opClass), opLat=op.opLat, pipelined=op.pipelined)
                for op in fu.opList
            ]
            new_fu_list.append(FUDesc(opList=new_ops, count=fu.count))
        else:
            new_fu_list.append(fu)
    return FUPool(FUList=new_fu_list)

# ── Cache classes (no gem5 stdlib dependency) ─────────────────────────────────

class L1ICache(Cache):
    assoc         = 8
    tag_latency   = 1
    data_latency  = 1
    response_latency = 1
    mshrs         = 4
    tgts_per_mshr = 20
    is_read_only  = True

class L1DCache(Cache):
    assoc         = 8
    tag_latency   = 4   # matches Horologium LoadHitLatency=4
    data_latency  = 4
    response_latency = 1
    mshrs         = 4
    tgts_per_mshr = 20

class L2Cache(Cache):
    assoc         = 16
    tag_latency   = 20
    data_latency  = 20
    response_latency = 20
    mshrs         = 20
    tgts_per_mshr = 12

# ── System ────────────────────────────────────────────────────────────────────

system = System(
    mem_mode    = "timing",
    mem_ranges  = [AddrRange(args.mem_size)],
    cache_line_size = 64,
)
# gem5 25.1 SE mode requires an explicit SE workload on the system.
# SEWorkload.init_compatible() reads the ELF header to pick the right workload type.
system.workload = SEWorkload.init_compatible(args.cmd)
system.voltage_domain    = VoltageDomain(voltage="1V")
system.clk_domain        = SrcClockDomain(
    clock=args.sys_clock, voltage_domain=system.voltage_domain
)
system.cpu_voltage_domain = VoltageDomain()
system.cpu_clk_domain    = SrcClockDomain(
    clock=args.cpu_clock, voltage_domain=system.cpu_voltage_domain
)

# ── O3CPU ─────────────────────────────────────────────────────────────────────
# gem5 25.1 RISCV build exposes RiscvO3CPU.

from m5.objects import RiscvO3CPU, RiscvISA

# Configure ISA for RV32 (default is RV64; must match the ELF ABI)
isa = RiscvISA(riscv_type="RV32")
cpu = RiscvO3CPU(isa=[isa])
cpu.clk_domain = system.cpu_clk_domain

# Stage widths
cpu.fetchWidth   = args.width
cpu.decodeWidth  = args.width
cpu.renameWidth  = args.width
cpu.dispatchWidth = args.width
cpu.issueWidth   = args.width
cpu.commitWidth  = args.width

# Buffers and queues
cpu.numROBEntries = args.rob
cpu.LQEntries     = args.lq
cpu.SQEntries     = args.sq

# IQ: gem5 25.1 uses a vector of IQUnit; one IQUnit with --iq entries.
# (Default is one IQUnit with 64 entries; we shrink to match Horologium.)
cpu.instQueues[0].numEntries = args.iq

# Physical register file
cpu.numPhysIntRegs   = args.phys_int
cpu.numPhysFloatRegs = args.phys_fp

# Branch predictor: use gem5's default TournamentBP + SimpleBTB + RAS(16).
# RAS size defaults to 16 entries in gem5 25.1, matching Horologium's RAS.

# Functional units: DefaultFUPool with optional IntDiv latency override.
cpu.fuPool = make_fu_pool(args.div_lat)

# ── Workload (SE process) ─────────────────────────────────────────────────────

process = Process()
process.executable = args.cmd
cmd_tokens = [args.cmd]
if args.args:
    cmd_tokens += args.args.split()
process.cmd = cmd_tokens

cpu.workload = process
cpu.createThreads()
cpu.createInterruptController()

system.cpu = cpu

# ── Cache hierarchy ───────────────────────────────────────────────────────────

system.l1i = L1ICache(size=args.l1i_size, clk_domain=system.cpu_clk_domain)
system.l1d = L1DCache(size=args.l1d_size, clk_domain=system.cpu_clk_domain)

cpu.icache_port = system.l1i.cpu_side
cpu.dcache_port = system.l1d.cpu_side

system.membus = SystemXBar()
system.system_port = system.membus.cpu_side_ports

if args.l2cache:
    system.l2   = L2Cache(size=args.l2_size, clk_domain=system.cpu_clk_domain)
    system.tol2 = L2XBar(clk_domain=system.cpu_clk_domain)
    system.l1i.mem_side = system.tol2.cpu_side_ports
    system.l1d.mem_side = system.tol2.cpu_side_ports
    system.l2.cpu_side  = system.tol2.mem_side_ports
    system.l2.mem_side  = system.membus.cpu_side_ports
else:
    system.l1i.mem_side = system.membus.cpu_side_ports
    system.l1d.mem_side = system.membus.cpu_side_ports

# ── Memory ────────────────────────────────────────────────────────────────────

system.mem_ctrl = SimpleMemory(
    range   = system.mem_ranges[0],
    port    = system.membus.mem_side_ports,
    latency = args.mem_lat_ns if args.mem_lat_ns else "30ns",
)

# ── Run ───────────────────────────────────────────────────────────────────────

root = Root(full_system=False, system=system)
m5.instantiate()

div_lat_str = str(args.div_lat) if args.div_lat else "20(default)"
mem_lat_str = args.mem_lat_ns if args.mem_lat_ns else "30ns(default)"
print(f"gem5 O3CPU SE mode: {args.cmd}  (width={args.width} ROB={args.rob} IQ={args.iq} DivLat={div_lat_str} MemLat={mem_lat_str})")
exit_event = m5.simulate()
print(f"Exit @ tick {m5.curTick()} because: {exit_event.getCause()}")
