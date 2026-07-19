{
  description = "Horologium: Execution Model";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";

  outputs =
    { self, nixpkgs }:
    let
      supportedSystems = [
        "x86_64-linux"
        "aarch64-linux"
      ];

      forEachSupportedSystem =
        f:
        nixpkgs.lib.genAttrs supportedSystems (
          system:
          f {
            pkgs = import nixpkgs {
              inherit system;
              config.allowUnfree = true;
            };
          }
        );

      # Olympia timing-model toolchain (Sparta + Olympia), packaged from source
      # since neither is in nixpkgs. Used for trace-driven timing co-simulation
      # against Horologium's OoO train. Each derivation lives in ./nix.
      olympiaToolchain =
        pkgs:
        let
          softfloat = pkgs.callPackage ./nix/softfloat.nix { };
          sparta = pkgs.callPackage ./nix/sparta.nix { };
          olympia = pkgs.callPackage ./nix/olympia.nix { inherit sparta softfloat; };
        in
        {
          inherit softfloat sparta olympia;
        };
    in
    {
      packages = forEachSupportedSystem (
        { pkgs }:
        let
          t = olympiaToolchain pkgs;
          opensbi-rv32 = pkgs.callPackage ./nix/opensbi-rv32.nix { };
          opensbi-rv64 = pkgs.callPackage ./nix/opensbi-rv64.nix { };
          linux-rv32   = pkgs.callPackage ./nix/linux-rv32.nix { };
          linux-rv64   = pkgs.callPackage ./nix/linux-rv64.nix { };
          gem5         = pkgs.callPackage ./nix/gem5.nix { };
          uve-spike    = pkgs.callPackage ./nix/uve-spike.nix { };
        in
        {
          inherit (t) softfloat sparta olympia;
          inherit opensbi-rv32 opensbi-rv64 linux-rv32 linux-rv64 gem5 uve-spike;
          default = t.olympia;
        }
      );

      devShells = forEachSupportedSystem (
        { pkgs }:
        let
          olympia = (olympiaToolchain pkgs).olympia;
          opensbi-rv32 = pkgs.callPackage ./nix/opensbi-rv32.nix { };
          opensbi-rv64 = pkgs.callPackage ./nix/opensbi-rv64.nix { };
          linux-rv32   = pkgs.callPackage ./nix/linux-rv32.nix { };
          linux-rv64   = pkgs.callPackage ./nix/linux-rv64.nix { };
          gem5         = pkgs.callPackage ./nix/gem5.nix { };
          uve-spike    = pkgs.callPackage ./nix/uve-spike.nix { };

          extra-path = with pkgs; [
            dotnetCorePackages.sdk_11_0-bin
            dotnet-sdk_11
            mono
            msbuild
            omnisharp-roslyn

            # RISC-V bare-metal toolchains (riscv{32,64}-none-elf-*): -nostdlib/-nostartfiles
            # only, no libc — used for the hand-assembled SE-mode fixtures under TestBinaries/.
            pkgsCross.riscv32-embedded.buildPackages.gcc
            pkgsCross.riscv32-embedded.buildPackages.binutils
            pkgsCross.riscv64-embedded.buildPackages.gcc
            pkgsCross.riscv64-embedded.buildPackages.binutils

            # RISC-V Linux userspace toolchain (riscv64-unknown-linux-musl-*): a real libc, a
            # real _start, argv/envp/auxv off the initial stack — the toolchain the batch-mode
            # harness (BenchmarkConfig/Experiment.RunBenchmark, InitialStackBuilder,
            # LinuxSyscallEmulator) has been unable to validate against for lack of exactly this.
            # musl over glibc deliberately: `-static` with pkgsCross.riscv64 (glibc) fails to
            # link out of the box here (no static libc output wired up without extra plumbing),
            # while musl statically links cleanly with no extra flags — confirmed by compiling
            # and linking a real hello-world (`riscv64-unknown-linux-musl-gcc -static`). Static
            # is required: Horologium has no dynamic-linker/loader support (AT_BASE is always 0).
            pkgsCross.riscv64-musl.buildPackages.gcc
            pkgsCross.riscv64-musl.buildPackages.binutils

            # RISC-V ISA reference simulator for lock-step co-simulation
            spike
            dtc # device tree compiler, required by spike

            # Olympia timing model (`olympia` on PATH), built by the flake — see
            # packages.olympia / nix/olympia.nix. Feed it a Horologium trace:
            #   dotnet run --project src/Apps/Runner -- <elf> --trace-json trace.json
            #   olympia trace.json
            olympia

            # OpenSBI RV32 generic firmware (`fw_jump.bin` on PATH via share/opensbi/).
            # Boot test: load fw_jump.bin with RawBinaryWorkload, pass VirtDtb.Bytes,
            # plant ebreak at 0x80200000, and check the UART captured "OpenSBI".
            # Built by: nix build .#opensbi-rv32
            opensbi-rv32

            # OpenSBI RV64 generic firmware — same layout as opensbi-rv32, built
            # against the riscv64-unknown-linux-gnu cross toolchain.
            # Built by: nix build .#opensbi-rv64
            opensbi-rv64

            # Linux 6.12 RV32 NOMMU kernel (`Image` on PATH via share/linux/).
            # Linux boot test: load fw_jump.bin + Image, check UART for "Linux version".
            # Built by: nix build .#linux-rv32
            linux-rv32

            # Linux 6.12 RV64 NOMMU kernel — same nommu_virt_defconfig as linux-rv32,
            # minus the 32-bit.config fragment, built against riscv64-unknown-linux-gnu.
            # Built by: nix build .#linux-rv64
            linux-rv64

            # gem5 RISCV with TraceCPU + protobuf (`gem5` on PATH).
            # Validate elastic-trace round-trips end-to-end:
            #   dotnet run --project src/Apps/Runner -- <elf> --elastic-record out.helf
            #   dotnet run --project src/Apps/Runner -- --elastic-to-gem5 out.helf out.gem5data
            #   dotnet run --project src/Apps/Runner -- --fetch-to-gem5   out.helf out.gem5fetch
            #   gem5 gem5-scripts/trace_cpu_riscv.py ...
            # Built by: nix build .#gem5
            gem5

            # UVE-extended Spike ISA simulator (AnaBSF/riscv-isa-sim, uve branch).
            # Reference for encoding verification against Horologium's UVE decoder.
            # Built by: nix build .#uve-spike
            uve-spike
          ];

          extra-lib = with pkgs; [
            skia
            harfbuzz
            fontconfig
            freetype
            libGL
            icu
            zlib
            libX11
            libXext
            libXrender
            libxcb
            libXi
            libXcursor
            libXrandr
            libice
            libsm
          ];

          ide = pkgs.writeShellApplication {
            name = "rider";
            runtimeInputs = extra-path;
            text = ''
              ${pkgs.jetbrains.rider}/bin/rider "$@"
            '';
          };
        in
        {
          default = pkgs.mkShell {
            packages = [ ide ] ++ extra-path ++ extra-lib;
            shellHook = ''
              export DOTNET_CLI_TELEMETRY_OPTOUT=1
              export DOTNET_NOLOGO=1

              export LD_LIBRARY_PATH=${pkgs.lib.makeLibraryPath extra-lib}:$LD_LIBRARY_PATH
            '';
          };

          # Minimal shell for CI: build + test with lock-step co-simulation.
          # Deliberately excludes the heavy from-source tools of the default
          # shell (gem5, Olympia, OpenSBI, Linux kernel, Rider) — everything
          # here substitutes from the nixpkgs binary cache.
          ci = pkgs.mkShell {
            packages = with pkgs; [
              dotnetCorePackages.sdk_11_0-bin
              spike
              dtc
            ];
            shellHook = ''
              export DOTNET_CLI_TELEMETRY_OPTOUT=1
              export DOTNET_NOLOGO=1
            '';
          };
        }
      );
    };
}
