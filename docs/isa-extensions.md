# ISA Extension Coverage

RV32/RV64 ISA extension coverage, privilege model, virtual memory, and interrupt dispatch. See
[project-layout.md](project-layout.md) for the top-level RiscV32/RiscV64 project summary, and [pipeline-trains.md](pipeline-trains.md)/
[out-of-order-execution.md](out-of-order-execution.md) for how instructions flow through the pipelines.

**ISA coverage:** I/M/A/F/D (standard), C (compressed 16-bit instructions), Zfh (half-precision FP: full
arithmetic/compare/classify/FMA/conversion set, NaN-boxed with the upper 48 bits of the unified register set to 1),
Zba (address generation: sh1add/sh2add/sh3add), Zbb (basic bit manipulation: andn/orn/xnor, clz/ctz/cpop,
min/minu/max/maxu, rol/ror/rori, sext.b/sext.h/zext.h, orc.b/rev8), Zbc (carry-less multiply: clmul/clmulh/clmulr),
Zbs (single-bit: bclr/bext/binv/bset and immediate forms), Zicond (czero.eqz/czero.nez), Zawrs (wrs.nto/wrs.sto — NOP in
single-core), Zicbom (cbo.inval/clean/flush — NOP), Zicboz (cbo.zero — zeros 64-byte cache-line-aligned block), Zicbop (
prefetch.i/r/w — NOP via ORI path), Zifencei (fence.i — NOP; I-cache invalidation on self-modifying code is not
modeled), Zimop (mop.r.N/mop.rr.N — return 0), Zcmop (c.mop.N, N odd 1–15 — compressed NOP hints, reserved C.LUI nzimm=0
encoding space), Zicntr (cycle/cycleh/time/timeh/instret/instreth user-level counter shadows;
mcycle/mcycleh/minstret/minstreth M-mode counters; pipeline trains drive IArchState.OnCycle()/OnRetire() hooks), V (
vector, VLEN=128, V1.0 fully implemented), and UVE (Unlimited Vector Extension, scalar subset; target spec is UVE2 —
Fernandes, U. Coimbra 2025 — with the AnaBSF/riscv-isa-sim uve branch as reference implementation). The UVE scalar
subset (encoded in RISC-V custom-0/custom-1 opcode space) covers: stream setup (`ss.sta.ld.w`, `ss.sta.st.w`, `ss.app`,
`ss.end`, `ss.cfg.vec` — dimensions configured outermost-first, Spike deque order, with `ss.end` adding the innermost;
modifier and vec-dim indices are likewise outermost-first, remapped to the engine's innermost-first order at `ss.end`);
stream modifiers with UVE2 semantics — `ss.app.mod` (static) and `ss.app.ind` (indirect) attach to the most recently
configured dimension as trigger and carry an explicit target dimension (`tdim`); the target's fields reset to configured
values when the trigger dimension itself wraps; Offset displacements and indirect offset values are element-scaled;
scalar broadcast (`so.v.dp.w`); element-wise FP arithmetic (`so.a.mul.fp`, `so.a.add.fp`, `so.a.sub.fp`, `so.a.mac.fp`);
stream-loop branches (`so.b.nc`, `so.b.ndc.D`); SO_P predicate register file (16 registers, VLEN/8=16 bytes each, reg 0
all-ones; `so.p.{zero,one,vr,not,mv,mvt}` simple manipulation ops with governing predicate and zeroing mode;
`so.p.{ge,eq,lt}.{us,fp,sg}` element-wise comparisons and `_z` zeroing-mode variants); `so.v.mv` / `so.v.mvt`
predicate-gated vector register move/transpose. `StreamDescriptor` holds N-dim `StreamDimension[]`; `StreamingEngine`
tracks per-dim consume-side pass-complete flags for `so.b.ndc.D` loop control. Stream-loop dimension branches (
`so.b.ndc.D` / `so.b.dc.D`) count dimensions from the outermost with funct3 = D−1 (Spike EODTable convention); the
pipeline remaps to the engine's innermost-first index. `UveStoreStream` supports N-dimensional layouts with per-dim
index carry, matching `StreamState` for load streams. Configure-once kernels are exercised end-to-end through the OoO
pipeline: 3D-stream GEMM with stride-0 repeat dimensions (`GemmTests`), lower-triangular sum via a single `ss.app.mod`
Size modifier (`TriangularSumModifierTests`), and sparse·dense dot product via `ss.sta.ld.w_inds` + `ss.app.ind`
indirect gather (`SparseDotProductTests`), alongside SAXPY and per-row-reconfiguring triangular/trisolv variants.

Scalar cryptography (RISC-V Cryptography Extensions Volume I: Scalar & Entropy Source Instructions, v1.0.1): Zknd
(NIST AES decryption: RV32 `aes32dsi`/`aes32dsmi`, RV64 `aes64ds`/`aes64dsm`/`aes64im`/`aes64ks1i`/`aes64ks2`), Zkne
(NIST AES encryption: RV32 `aes32esi`/`aes32esmi`, RV64 `aes64es`/`aes64esm`, sharing `aes64ks1i`/`aes64ks2` with
Zknd), Zknh (NIST SHA2: `sha256sig0`/`sha256sig1`/`sha256sum0`/`sha256sum1` common to both widths, plus the RV32
split-register SHA2-512 forms `sha512sig0h`/`sha512sig0l`/`sha512sig1h`/`sha512sig1l`/`sha512sum0r`/`sha512sum1r` and
their RV64 direct-64-bit counterparts `sha512sig0`/`sha512sig1`/`sha512sum0`/`sha512sum1`), Zksed (ShangMi SM4:
`sm4ed`/`sm4ks`, common to both widths), Zksh (ShangMi SM3: `sm3p0`/`sm3p1`, common to both widths), and Zkr (the
`seed` entropy-source CSR at 0x015, modeled as a virtual entropy source per §4.2.3 — every poll succeeds with fresh
deterministic pseudorandomness, default M-mode-only access). RV32 and RV64 use disjoint AES/SHA2-512 encodings by
design (`Rv64Decoder` explicitly rejects the RV32-only forms); the width-shared instructions (SHA2-256, SM3, SM4)
sign-extend their 32-bit result to XLEN on RV64 via a `(int)`-cast in the shared RV32 executor code, which RV64
inherits unmodified.

The same spec volume's bitmanip-for-crypto subset is also implemented: Zbkb (`pack`/`packh`, RV64-only `packw`,
`brev8` per-byte bit reversal, and the RV32-only `zip`/`unzip` bit-interleave pair), Zbkx (`xperm4`/`xperm8`
crossbar nibble/byte permutation), and Zbkc (fully satisfied by the pre-existing Zbc `clmul`/`clmulh` — no new
code needed). `pack`/`brev8`/`xperm4`/`xperm8` are width-dependent (half-width/byte-count/element-count doubles
under RV64's `Rv64Executor` override); `pack rd, rs1, x0` in particular needed an explicit RV64 decoder
interception, since RV32's decoder special-cases `rs2=0` as the narrower Zbb `zext.h`.

Vector cryptography (RISC-V Cryptography Extensions Volume II: Vector Instructions, v1.0.0): the full Zvkned
AES block-cipher extension — encrypt (`vaesem.vv/.vs`, `vaesef.vv/.vs`), decrypt (`vaesdm.vv/.vs`,
`vaesdf.vv/.vs`), round-zero (`vaesz.vs`), and forward key schedule (`vaeskf1.vi` AES-128, `vaeskf2.vi`
AES-256) — operating on 128-bit "element groups" (EGS=4 consecutive 32-bit elements forming one AES block)
rather than single SEW-wide elements. This introduced the general element-group architecture that any future
vector-crypto instruction can build on: `ITooth.HasRuntimeSizedVectorDestination` flags instructions whose
destination register *count* depends on runtime `vtype`/LMUL rather than the encoding. `OooTrain` needs no
changes at all — head-serializing every `ToothClass.Vector` op is already sufficient regardless of how many
registers get written. `FiveStageTrain` widens its vector RAW hazard check with a runtime LMUL-derived
register span instead: `ITooth.RuntimeVectorRegisterSpan(baseRegister, state)` gives an in-flight producer's
precise span (its own LMUL is always already resolved by hazard-check time), while
`ITooth.MaxRuntimeVectorRegisterSpan(baseRegister)` gives a state-independent conservative maximum for the
not-yet-decoded consumer side, whose LMUL could still change from a `vsetvli` sitting in a pipeline latch
this very cycle. The element-group architecture also added constraint checking for the spec's §1.5 rules
(LMUL·VLEN≥EGW, SEW matching, `vl`/`vstart` multiples of EGS), and a register-group-range reserved-encoding
check for the `.vs` forms (vd's LMUL group must not overlap the scalar vs2 register). Vector-crypto
instructions use a dedicated major opcode (`0x77`), not the
standard OP-V opcode (`0x57`) despite an otherwise identical field layout — caught via a
`riscv64-none-elf-as`/objdump round-trip and confirmed against the `riscv-opcodes` project, after the
spec-text extraction assumed 0x57. `vaesdm.vv`'s round-key XOR lands *before* InvMixColumns — unlike every
other round op, where the XOR is the final step — matching the spec pseudocode and FIPS-197's Equivalent
Inverse Cipher (§5.3.5) construction. Validated against the FIPS-197 Appendix A.1 key-expansion and Appendix B
cipher-example traces.

The full Zvksed extension (SM4 block cipher) reuses this same element-group infrastructure unchanged: round
function `vsm4r.vv/.vs` and key expansion `vsm4k.vi`, validated against GB/T 32907-2016 Example 1
(key==plaintext, via `draft-ribose-cfrg-sm4`). SM4's own final word-order-reversing "R transformation" is not
part of `vsm4r`/`vsm4k` themselves — applied by the caller (the test, here), matching the spec. Surfaced a
real bug in the shared `ElementGroupGetWord`/`ElementGroupSetWord` word-packing helpers: they originally
packed bytes LSB-first, which is transparent to AES's key schedule (only ever rotates by a whole byte) but
silently wrong for SM4's non-byte-aligned rotations (2/10/13/18/23 bits); fixed to natural big-endian packing,
with AES's key-schedule instructions updated to match (rotate direction flipped, the shared `AesRcon` table
shifted into the high byte at the point of use, without touching the table itself since it's shared with the
scalar `aes64ks1i` instruction).

The Zvknha/Zvknhb extension (SHA-2 compression + message schedule) implements the Zvknhb superset
unconditionally: `vsha2ch.vv`/`vsha2cl.vv` (two rounds of compression) and `vsha2ms.vv` (four rounds of
message-schedule expansion), accepting both SEW=32 (SHA-256) and SEW=64 (SHA-512) rather than gating the
64-bit path behind a separate hart-extension flag. Unlike every other Zvk* op, EGW=4·SEW is itself
runtime-dependent (128 for SHA-256, 256 for SHA-512 — the latter needing 2 physical registers per element
group, which the existing `ReadElementGroup`/`WriteElementGroup` helpers already handled correctly since they
took `egwBits` as a runtime parameter from the start), and `vs1` is a genuine third vector source register
rather than a sub-op selector or immediate. The element-index-to-named-variable mapping (which element holds
`a`, `b`, `e`, `f`, etc.) was confirmed against the RISC-V Sail reference model
(`github.com/riscv/sail-riscv`, `model/extensions/vector_crypto/zvknhab_insts.sail`) rather than derived from
the spec's prose concatenation notation alone, which is genuinely ambiguous without seeing how `get_velem`
actually indexes elements. Validated end-to-end — multi-block, chaining `vsha2ms` with `vsha2ch`/`vsha2cl`
through a complete SHA-256/SHA-512 hash — against `System.Security.Cryptography.SHA256`/`SHA512`, a real
independently-implemented oracle, since FIPS 180-4's on-disk text has no worked example with intermediate
round values to hand-transcribe (same situation as FIPS-197's Appendix C).

The Zvksh extension (SM3 secure hash) adds `vsm3c.vi` (two rounds of compression) and `vsm3me.vv` (eight
rounds of message-schedule expansion), with a fixed EGW=256/EGS=8/SEW=32 shape — a new element-group size
(8, not 4) distinct from every earlier Zvk* op. Validated against the full GB/T 32905-2016 Example 1 and
Example 2 round-by-round traces (via IETF draft-sca-cfrg-sm3, since no built-in .NET SM3 oracle exists like
SHA-2 had): both single-instruction checks in isolation (localizing the round math and element ordering
independently) and complete multi-block end-to-end digests, the second of which crosses a block boundary to
exercise SM3's feed-forward finalization (`V_(i+1) = CF(V_i, B_i) xor V_i`, an XOR rather than SHA-2's modular
addition). Element ordering was confirmed against the RISC-V Sail reference model rather than the spec's own
prose tables, which use the opposite left-to-right listing convention from every other instruction in the
same document. `vsm3c.vi`'s round function is implemented in the straightforward plain (A,B,C,D,E,F,G,H)
order rather than porting the Sail source's own shuffled return-vector shape verbatim — both are
output-equivalent (confirmed byte-exact against the traces), and the plain form avoids depending on a Sail
vector-literal indexing detail this port doesn't otherwise need to resolve.

The Zvkg extension (vector GCM/GMAC) adds `vghsh.vv` (one GHASH add-multiply iteration,
Yi+1 = (Yi ^ Xi) * H over GF(2^128)) and `vgmul.vv` (one GHASH multiply, Y * H — sharing
`vaesem.vv`/`vsm4r.vv`'s funct6, disambiguated by a hardcoded vs1=0x11 rather than a real register
operand), reusing the EGW=128/EGS=4/SEW=32 shape of Zvkned/Zvksed. Unlike every other Zvk* op, the
whole 128-bit element group is one GF(2^128) polynomial with no sub-word decomposition, and neither
op has a register-overlap reserved encoding (confirmed against the Sail encdec guard, which calls no
`zvk_valid_reg_overlap` for either instruction — the first Zvk* ops without one). Validated against
the McGrew-Viega GCM specification's Test Case 4, which — unlike NIST SP 800-38D — publishes the raw
intermediate `GHASH(H, A, C)` value directly, letting `vghsh.vv` be chained across real nonzero
AAD/ciphertext/length blocks and checked byte-exact without needing a full AES-CTR encryption
harness; `vgmul.vv` cross-checked against `vghsh.vv` called with an all-zero vs1 (X=0).

The Zvbb/Zvbc/Zvkb extensions (vector basic bit-manipulation / carryless multiply) add `vandn`,
`vrol`/`vror` (`.vv`/`.vx`, plus `.vi` for `vror`), `vwsll` (`.vv`/`.vx`/`.vi`), the VXUNARY0 unary
group `vbrev8.v`/`vrev8.v`/`vbrev.v`/`vclz.v`/`vctz.v`/`vcpop.v`, and `vclmul`/`vclmulh`
(`.vv`/`.vx`). Unlike every other Zvk* family, these live on the standard OP-V opcode (0x57) and
operate per-element (EEW=SEW) exactly like the base V-extension integer ALU/multiply ops, rather
than the dedicated crypto opcode (0x77) with element-group (EGW/EGS/`get_velem`) semantics — so
they extend the existing `VIntOp`/`VWideOp` shapes instead of the crypto element-group
infrastructure. Zvkb is a proper subset of Zvbb (`vandn`, `vbrev8`, `vrev8`, `vrol`, `vror`) with
no encodings of its own, per the spec text, so implementing Zvbb covers it with no additional
code. `vror.vi`'s encoding steals bit 26 (normally the funct6 LSB) as immediate bit 5 — the top 5
bits (31:27) select the opcode, and the stolen bit concatenates with the 5-bit rs1 field to form a
6-bit rotate amount (0-63, needed since SEW can be 64) — handled by intercepting the raw bit
pattern before the generic funct6-based dispatch runs, since the generically-computed funct6
would otherwise fold to the same value as `vrol.vv`/`vx`'s real funct6. Adding SEW=64 support
(required by `vrol`/`vror`/`vclz`/etc.) uncovered two latent bugs: `ApplyVIntOp`'s bit-mask
computation silently produced 0 at SEW=64 (C#'s ulong-shift-count-mod-64 rule turns `1UL << 64`
into `1UL << 0`), invisible until now since every pre-existing `VIntOp` member is
carry-safe/low-bit-independent; and `ReadVElement`'s ewBytes==4 path can sign-extend a
high-bit-set byte through an `int` cast, invisible to arithmetic ops but corrupting the new
bit-magnitude-sensitive ops, fixed with a defensive re-mask at the call site rather than touching
the shared read helper. `vclmul`/`vclmulh` were validated against hand-derived GF(2)[x]
polynomial identities rather than the implementation's own loop. This closes out the entire
RISC-V Vector Cryptography Extensions Volume II instruction set.

**Privilege model:** Three privilege levels (User=0, Supervisor=1, Machine=3). Trap delegation: when an exception's
`medeleg` bit is set and the hart is below Machine privilege, `RaiseTrap` enters S-mode (writes `sepc`/`scause`/`stval`,
updates `sstatus` SPP/SPIE/SIE, sets privilege to Supervisor, returns `stvec` base); otherwise the existing M-mode path
applies. Interrupt causes (bit 31 set in mcause/scause) are delegated via `mideleg` rather than `medeleg`. `MRET` is
guarded to Machine mode; `SRET` requires at least Supervisor. `ECALL` emits the correct cause code for the current
privilege level (8=U, 9=S, 11=M). CSR accesses from an insufficient privilege level or writes to read-only CSRs raise
`IllegalInstruction`.

**Virtual memory (Sv32):** The `satp` CSR (0x180, Supervisor-mode) controls address translation. When `satp.MODE=1`,
both instruction fetch and data loads/stores go through a two-level Sv32 page table walk (`Sv32Walker`). `MODE=0` (bare)
uses virtual address = physical address and preserves the existing behaviour for all benchmark workloads. A/D bits are
enforced using a fault-on-access model: `A=0` or (`D=0` on a store) raises the corresponding page fault (
`LoadPageFault`/`StorePageFault`). Supervisor User Memory (SUM) is not implemented; S-mode always faults on user pages (
PTE.U=1). Instruction fetch translation crosses the ISA isolation boundary via the `IFetchTranslator` interface in
src/Core/Mechanism/: `RvFetchTranslator` (in src/Isa/RiscV32/) calls `Sv32Walker` with `isExec=true`, returning
`(physAddr, 0)` on success or `(0, 12)` for `InstructionPageFault`; M-mode fetches bypass the walk entirely (RISC-V priv
spec §3.1.6). All four pipeline trains wire up the translator and propagate the pre-baked `TrapInfo` through the
pipeline latch chain to Writeback, where it is raised via the normal trap path.

**Interrupt dispatch:** `ITrapController.PeekInterrupt(IArchState)` returns the highest-priority pending interrupt (per
the RISC-V §3.1.9 priority order: MEI > MSI > MTI > SEI > SSI > STI) when `mip & mie` has a pending bit and the global
interrupt enable for the current privilege and delegation state permits delivery. All four pipeline trains call
`PeekInterrupt` at each retire/commit boundary and invoke `RaiseTrap` when a non-null result is returned. `mideleg`
routes delegated interrupts to S-mode.
