// Multi-way sequential stream-buffer prefetcher (Jouppi, ISCA 1990) — the first RTL
// prefetcher issuing multiple targets per access, driven through RtlFfiPrefetcher
// (src/Core/Orrery/Cache) via rtl_mpf_shim.cpp.
//
// Deliberately mirrors Horologium's C# StreamPrefetcher bit-for-bit so the
// differential co-sim test can demand identical prefetch counts and identical target
// sequences on identical access streams:
//   - streamCount independent buffers of (demandLine, prefetchFront, lruAge, valid)
//   - first stream (lowest index) whose demandLine + blockBytes matches the accessed
//     line advances (on hits and misses alike) and issues one line to keep the
//     frontier exactly depth lines ahead
//   - no match + miss → allocate: first invalid slot, else strictly-least lruAge
//     (earliest index on ties), and burst-issue depth lines at once
//   - lruAge comes from a shared tick counter incremented per match/allocation
//
// Multi-target port contract: the access edge loads an internal drain queue (up to
// depth addresses); io_drainValid/io_drainAddr present the head, and asserting
// io_drainPop pops one entry per clock edge. The shim drains after each access —
// the rtl_pf_* C ABI is unchanged, so the C# side needs nothing new. The caller's
// target buffer must hold at least `depth` entries (Horologium's pipelines pass
// buffers sized for the configured depth); undrained entries are dropped at the
// next access.
//
// scala-cli project directives live in DivUnit.scala. Regenerate generated/StreamPf.sv
// with ./generate.sh after editing.

package rtlfu

import chisel3._
import chisel3.util._

class StreamPf(streamCount: Int = 4, depth: Int = 8, blockBytes: Int = 32) extends Module {
  require(isPow2(blockBytes) && depth >= 1)

  val io = IO(new Bundle {
    val cfgTableSize = Output(UInt(32.W)) // stream count

    val accValid = Input(Bool())
    val accPc    = Input(UInt(64.W))
    val accAddr  = Input(UInt(64.W))
    val accHit   = Input(Bool())

    val drainValid = Output(Bool())
    val drainAddr  = Output(UInt(64.W))
    val drainPop   = Input(Bool())
  })

  io.cfgTableSize := streamCount.U

  val demandLine    = RegInit(VecInit(Seq.fill(streamCount)(0.U(64.W))))
  val prefetchFront = RegInit(VecInit(Seq.fill(streamCount)(0.U(64.W))))
  val lruAge        = RegInit(VecInit(Seq.fill(streamCount)(0.U(32.W))))
  val valid         = RegInit(VecInit(Seq.fill(streamCount)(false.B)))
  val tick          = RegInit(0.U(32.W))

  val qAddrs = Reg(Vec(depth, UInt(64.W)))
  val qHead  = RegInit(0.U(log2Ceil(depth + 1).W))
  val qCount = RegInit(0.U(log2Ceil(depth + 1).W))

  io.drainValid := qCount =/= 0.U
  io.drainAddr  := qAddrs(qHead(log2Ceil(depth) - 1, 0))

  when(io.drainPop && qCount =/= 0.U) {
    qHead  := qHead + 1.U
    qCount := qCount - 1.U
  }

  when(io.accValid) {
    val lineBase = io.accAddr & (~(blockBytes - 1).U(64.W)).asUInt
    val newTick  = tick + 1.U

    val matches = VecInit(
      (0 until streamCount).map(i => valid(i) && lineBase === demandLine(i) + blockBytes.U)
    )
    val anyMatch = matches.asUInt.orR
    val mi       = PriorityEncoder(matches) // first match in index order, like the C# scan

    qHead := 0.U
    when(anyMatch) {
      demandLine(mi) := lineBase
      lruAge(mi)     := newTick
      tick           := newTick

      // Issue one prefetch to keep the frontier exactly depth lines ahead.
      val front = prefetchFront(mi)
      when(front <= lineBase + (depth * blockBytes).U) {
        qAddrs(0)         := front
        qCount            := 1.U
        prefetchFront(mi) := front + blockBytes.U
      }.otherwise {
        qCount := 0.U // frontier already depth+ lines ahead — buffer full
      }
    }.elsewhen(!io.accHit) {
      // Allocate: first invalid slot, else strictly-least lruAge (earliest on ties).
      val invalids   = VecInit((0 until streamCount).map(i => !valid(i)))
      val anyInvalid = invalids.asUInt.orR
      val lruSlot = (1 until streamCount)
        .foldLeft((0.U(log2Ceil(streamCount).W), lruAge(0))) { case ((idx, age), i) =>
          val take = lruAge(i) < age
          (Mux(take, i.U, idx), Mux(take, lruAge(i), age))
        }
        ._1
      val slot = Mux(anyInvalid, PriorityEncoder(invalids), lruSlot)

      valid(slot)      := true.B
      demandLine(slot) := lineBase
      lruAge(slot)     := newTick
      tick             := newTick

      // Burst-issue depth lines to fill the conceptual buffer (Jouppi §4.1).
      for (k <- 0 until depth) qAddrs(k) := lineBase + ((k + 1) * blockBytes).U
      qCount              := depth.U
      prefetchFront(slot) := lineBase + ((depth + 1) * blockBytes).U
    }.otherwise {
      qCount := 0.U // hit with no stream match — drop any stale queue
    }
  }
}

/** Emits generated/StreamPf.sv. Run via generate.sh. */
object GenerateStream extends App {
  _root_.circt.stage.ChiselStage.emitSystemVerilogFile(
    new StreamPf,
    Array("--target-dir", "generated"),
    Array("--lowering-options=disallowLocalVariables,disallowPackedArrays", "--strip-debug-info")
  )
}
