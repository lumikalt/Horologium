# OpenSBI generic platform for RV32.
# Produces fw_jump.bin: OpenSBI boot firmware that prints its banner and then
# jumps to FW_JUMP_ADDR (0x80200000). No payload is embedded.
# The caller is responsible for placing code at FW_JUMP_ADDR or using a tick limit.
#
# FW_JUMP_FDT_ADDR is left unset so OpenSBI uses a1 from the previous stage
# (i.e., whatever the Horologium test passes in register a1).
#
# Note: uses pkgsCross.riscv32 (riscv32-unknown-linux-gnu) rather than the
# bare-metal riscv32-embedded toolchain. The linux-gnu toolchain supports PIE,
# which OpenSBI ≥ 1.2 requires. It still produces valid bare-metal binaries.
#
# Build:   nix build .#opensbi-rv32
# Output:  result/share/opensbi/fw_jump.{bin,elf}
{
  lib,
  stdenv,
  fetchFromGitHub,
  python3,
  pkgsCross,
}:

let
  rv32 = pkgsCross.riscv32;
in
stdenv.mkDerivation {
  pname = "opensbi-rv32-virt";
  version = "1.8.1";

  src = fetchFromGitHub {
    owner = "riscv-software-src";
    repo = "opensbi";
    tag = "v1.8.1";
    hash = "sha256-nD22UZfH0rJECHMDwd9ATyLz44cFHqcFH7N6piK8hog=";
  };

  postPatch = ''
    patchShebangs ./scripts
  '';

  # python3 for host-side build scripts; rv32 linux-gnu gcc for cross-assembly
  # (linux-gnu toolchain supports PIE, required by OpenSBI ≥ 1.2)
  nativeBuildInputs = [
    python3
    rv32.buildPackages.gcc
    rv32.buildPackages.binutils
  ];

  makeFlags = [
    "CROSS_COMPILE=riscv32-unknown-linux-gnu-"
    "PLATFORM=generic"
    # Jump to S-mode kernel at 0x80200000 after banner.
    # Plant an ebreak there in your test to stop simulation cleanly.
    "FW_JUMP_ADDR=0x80200000"
    # FW_JUMP_FDT_ADDR intentionally omitted: OpenSBI will use a1 (DTB address)
    # passed by the prior boot stage (the Horologium test harness).
  ];

  enableParallelBuilding = true;
  dontStrip = true;
  dontPatchELF = true;

  installPhase = ''
    mkdir -p $out/share/opensbi
    cp build/platform/generic/firmware/fw_jump.bin $out/share/opensbi/
    cp build/platform/generic/firmware/fw_jump.elf $out/share/opensbi/
  '';

  meta = {
    description = "OpenSBI fw_jump for RV32 generic platform (Horologium virt machine)";
    homepage = "https://github.com/riscv-software-src/opensbi";
    license = lib.licenses.bsd2;
    platforms = [
      "x86_64-linux"
      "aarch64-linux"
    ];
  };
}
