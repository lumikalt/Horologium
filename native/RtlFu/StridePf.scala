// Reference Prediction Table (RPT) stride prefetcher — the first RTL prefetcher
// substitution target, driven through RtlFfiPrefetcher (src/Core/Orrery/Cache) via
// rtl_pf_shim.cpp.
//
// Deliberately mirrors Horologium's C# StridePrefetcher bit-for-bit so the
// differential co-sim test can demand identical prefetch decisions on identical
// access streams:
//   - table of 2^n entries indexed by (pc >> 2) & mask
//   - per-entry (lastAddr, stride, 2-bit confidence, initialized)
//   - first access from a PC only records lastAddr (no stride from the zero init)
//   - same stride seen again → confidence++, else stride := new, confidence--
//   - prefetch address + stride when post-update confidence ≥ 2 and stride ≠ 0
// The prefetch decision uses the post-update entry (C# updates then checks), so
// io_prefValid/io_prefAddr are computed combinationally from current state + input
// and the table commits on the same clock edge. 64-bit datapath: stride arithmetic
// wraps mod 2^64 exactly like the C# long subtraction.
//
// io_accHit is part of the port contract (IPrefetcher receives it) but, like the
// C# stride prefetcher, this model ignores it.
//
// scala-cli project directives live in DivUnit.scala. Regenerate generated/StridePf.sv
// with ./generate.sh after editing.

package rtlfu

import chisel3._
import chisel3.util._

class StridePf(tableSize: Int = 64) extends Module {
  require(isPow2(tableSize))

  val io = IO(new Bundle {
    val cfgTableSize = Output(UInt(32.W))

    val accValid = Input(Bool())
    val accPc    = Input(UInt(64.W))
    val accAddr  = Input(UInt(64.W))
    val accHit   = Input(Bool())

    val prefValid = Output(Bool())
    val prefAddr  = Output(UInt(64.W))
  })

  io.cfgTableSize := tableSize.U

  val lastAddr = RegInit(VecInit(Seq.fill(tableSize)(0.U(64.W))))
  val strideR  = RegInit(VecInit(Seq.fill(tableSize)(0.U(64.W)))) // two's-complement
  val conf     = RegInit(VecInit(Seq.fill(tableSize)(0.U(2.W))))
  val init     = RegInit(VecInit(Seq.fill(tableSize)(false.B)))

  val idx = (io.accPc >> 2)(log2Ceil(tableSize) - 1, 0)

  val stride    = io.accAddr - lastAddr(idx) // wraps mod 2^64, like C# long arithmetic
  val same      = stride === strideR(idx)
  val newConf   = Mux(
    same,
    Mux(conf(idx) === 3.U, 3.U, conf(idx) + 1.U),
    Mux(conf(idx) === 0.U, 0.U, conf(idx) - 1.U)
  )
  val newStride = Mux(same, strideR(idx), stride)

  // C# checks the post-update entry; the first access from a PC never prefetches.
  io.prefValid := io.accValid && init(idx) && newConf >= 2.U && newStride =/= 0.U
  io.prefAddr  := io.accAddr + newStride

  when(io.accValid) {
    when(!init(idx)) {
      init(idx)     := true.B
      lastAddr(idx) := io.accAddr
    }.otherwise {
      conf(idx)     := newConf
      strideR(idx)  := newStride
      lastAddr(idx) := io.accAddr
    }
  }
}

/** Emits generated/StridePf.sv. Run via generate.sh. */
object GeneratePf extends App {
  _root_.circt.stage.ChiselStage.emitSystemVerilogFile(
    new StridePf,
    Array("--target-dir", "generated"),
    Array("--lowering-options=disallowLocalVariables,disallowPackedArrays", "--strip-debug-info")
  )
}
