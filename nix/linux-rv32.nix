# Minimal Linux 6.12 kernel for RV32 NOMMU QEMU virt (Horologium simulation).
#
# Uses nommu_virt_defconfig + 32-bit.config fragment.  No MMU + M-mode means the
# kernel links at PAGE_OFFSET = 0x80000000 (RAM base), so it must be loaded there
# directly — no OpenSBI.
#
# The cmdline is overridden to drop root=/dev/vda so the kernel boots as far as
# possible without a rootfs.  The test only needs to see "Linux version" on the
# UART; the panic that follows when init is not found is intentional.
#
# Build:   nix build .#linux-rv32
# Output:  result/share/linux/Image   (plain binary, ~2 MiB)
#          result/share/linux/kernel.config
{
  lib,
  stdenv,
  fetchurl,
  pkgsCross,
  flex,
  bison,
  bc,
  openssl,
  perl,
  python3,
  cpio,
  gzip,
}:

let
  rv32 = pkgsCross.riscv32;
  xcc  = "riscv32-unknown-linux-gnu-";
in
stdenv.mkDerivation rec {
  pname   = "linux-rv32-nommu-virt";
  version = "6.12.94";

  src = fetchurl {
    url  = "https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-${version}.tar.xz";
    hash = "sha256-6ZiiMrlBjbMwHLWEaOKRpPQdargwYCmzDZkfViUdyNI=";
  };

  # Build tools: all must come from the build (x86) platform.
  nativeBuildInputs = [
    rv32.buildPackages.gcc
    rv32.buildPackages.binutils
    flex
    bison
    bc          # scripts/kconfig/mconf
    openssl     # certs/extract-cert
    perl        # various build scripts
    python3     # dtc integration scripts
    cpio        # initramfs packaging (not strictly needed without embedded initramfs)
    gzip
  ];

  enableParallelBuilding = true;
  dontStrip    = true;
  dontPatchELF = true;

  postPatch = ''
    patchShebangs scripts/
  '';

  configurePhase = ''
    # Base config: nommu_virt_defconfig (QEMU virt, no MMU, VirtIO, ns16550a UART)
    make ARCH=riscv CROSS_COMPILE=${xcc} nommu_virt_defconfig

    # Apply 32-bit fragment (sets CONFIG_ARCH_RV32I=y, CONFIG_32BIT=y)
    cat arch/riscv/configs/32-bit.config >> .config

    # Override the forced cmdline: drop root=/dev/vda, keep earlycon + console.
    # earlycon=uart8250,mmio drives the ns16550a at 0x10000000 (byte-addressable
    # per reg-shift=0 in virt.dts) so kernel log appears on UART from the start.
    ./scripts/config \
      --set-str CONFIG_CMDLINE "earlycon=uart8250,mmio,0x10000000,115200n8 console=ttyS0"

    # Resolve any config conflicts introduced by appending 32-bit.config.
    make ARCH=riscv CROSS_COMPILE=${xcc} olddefconfig
  '';

  buildPhase = ''
    make ARCH=riscv CROSS_COMPILE=${xcc} Image -j$NIX_BUILD_CORES
  '';

  installPhase = ''
    mkdir -p $out/share/linux
    cp arch/riscv/boot/Image $out/share/linux/
    cp .config $out/share/linux/kernel.config
  '';

  meta = {
    description = "Linux ${version} — RV32 NOMMU QEMU virt for Horologium simulation";
    homepage    = "https://kernel.org";
    license     = lib.licenses.gpl2Only;
    platforms   = [ "x86_64-linux" "aarch64-linux" ];
  };
}
