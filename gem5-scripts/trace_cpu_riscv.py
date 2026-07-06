"""
gem5 TraceCPU replay script for Horologium HELF elastic traces.

Usage (after converting both trace files):
  gem5 gem5-scripts/trace_cpu_riscv.py \\
      --data-trace-file  out.gem5data \\
      --inst-trace-file  out.gem5fetch \\
      [--mem-size 512MB] [--cpu-clock 1GHz] [--l1d-size 32kB] [--l2cache]

Generate traces from an ELF binary:
  dotnet run --project Runner -- <elf> --elastic-record out.helf
  dotnet run --project Runner -- --elastic-to-gem5 out.helf out.gem5data
  dotnet run --project Runner -- --fetch-to-gem5   out.helf out.gem5fetch
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
    TraceCPU,
    VoltageDomain,
)

# ── L1/L2 cache helpers ──────────────────────────────────────────────────────
# Inline the basic cache objects so this script has no dependency on gem5's
# configs/common path (which varies by install).

from m5.objects import Cache, L2XBar


class L1ICache(Cache):
    assoc = 8
    tag_latency = 2
    data_latency = 2
    response_latency = 2
    mshrs = 4
    tgts_per_mshr = 20
    is_read_only = True


class L1DCache(Cache):
    assoc = 8
    tag_latency = 2
    data_latency = 2
    response_latency = 2
    mshrs = 4
    tgts_per_mshr = 20


class L2Cache(Cache):
    assoc = 16
    tag_latency = 20
    data_latency = 20
    response_latency = 20
    mshrs = 20
    tgts_per_mshr = 12


# ── Argument parsing ─────────────────────────────────────────────────────────

parser = argparse.ArgumentParser(
    description="Replay Horologium HELF elastic traces through gem5 TraceCPU."
)
parser.add_argument("--data-trace-file", required=True,
                    help="gem5 inst_dep_record.proto data trace (--elastic-to-gem5)")
parser.add_argument("--inst-trace-file", required=True,
                    help="gem5 packet.proto fetch trace (--fetch-to-gem5)")
parser.add_argument("--cpu-clock",  default="1GHz",
                    help="CPU clock frequency (default: 1GHz)")
parser.add_argument("--sys-clock",  default="1GHz",
                    help="System / bus clock frequency (default: 1GHz)")
parser.add_argument("--mem-size",   default="512MB",
                    help="Physical memory size (default: 512MB)")
parser.add_argument("--l1i-size",   default="32kB")
parser.add_argument("--l1d-size",   default="32kB")
parser.add_argument("--l2cache",    action="store_true",
                    help="Add an L2 cache")
parser.add_argument("--l2-size",    default="256kB")
parser.add_argument("--rob-size",   type=int, default=40,
                    help="TraceCPU ROB entries (default: 40)")
parser.add_argument("--load-buf",   type=int, default=16,
                    help="TraceCPU load buffer entries (default: 16)")
parser.add_argument("--store-buf",  type=int, default=16,
                    help="TraceCPU store buffer entries (default: 16)")

args = parser.parse_args()

# ── System ────────────────────────────────────────────────────────────────────

system = System(
    mem_mode="timing",
    mem_ranges=[AddrRange(args.mem_size)],
    cache_line_size=64,
)

system.voltage_domain    = VoltageDomain(voltage="1V")
system.clk_domain        = SrcClockDomain(
    clock=args.sys_clock, voltage_domain=system.voltage_domain
)
system.cpu_voltage_domain = VoltageDomain()
system.cpu_clk_domain    = SrcClockDomain(
    clock=args.cpu_clock, voltage_domain=system.cpu_voltage_domain
)

# ── TraceCPU ──────────────────────────────────────────────────────────────────

system.cpu = TraceCPU()
system.cpu.clk_domain    = system.cpu_clk_domain
system.cpu.dataTraceFile = args.data_trace_file
system.cpu.instTraceFile = args.inst_trace_file
system.cpu.sizeROB        = args.rob_size
system.cpu.sizeLoadBuffer = args.load_buf
system.cpu.sizeStoreBuffer = args.store_buf

# ── Cache hierarchy ───────────────────────────────────────────────────────────

system.l1i = L1ICache(size=args.l1i_size, clk_domain=system.cpu_clk_domain)
system.l1d = L1DCache(size=args.l1d_size, clk_domain=system.cpu_clk_domain)

system.cpu.icache_port = system.l1i.cpu_side
system.cpu.dcache_port = system.l1d.cpu_side

system.membus = SystemXBar()
system.system_port = system.membus.cpu_side_ports

if args.l2cache:
    system.l2    = L2Cache(size=args.l2_size, clk_domain=system.cpu_clk_domain)
    system.tol2  = L2XBar(clk_domain=system.cpu_clk_domain)
    system.l1i.mem_side = system.tol2.cpu_side_ports
    system.l1d.mem_side = system.tol2.cpu_side_ports
    system.l2.cpu_side  = system.tol2.mem_side_ports
    system.l2.mem_side  = system.membus.cpu_side_ports
else:
    system.l1i.mem_side = system.membus.cpu_side_ports
    system.l1d.mem_side = system.membus.cpu_side_ports

# ── Memory ────────────────────────────────────────────────────────────────────

system.mem_ctrl = SimpleMemory(
    range=system.mem_ranges[0],
    port=system.membus.mem_side_ports,
    latency="30ns",
)

# ── Run ───────────────────────────────────────────────────────────────────────

root = Root(full_system=False, system=system)
m5.instantiate()
print(f"gem5 TraceCPU replay: {args.data_trace_file} + {args.inst_trace_file}")
exit_event = m5.simulate()
print(f"Exiting @ tick {m5.curTick()} because: {exit_event.getCause()}")
