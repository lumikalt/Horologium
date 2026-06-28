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
    in
    {
      devShells = forEachSupportedSystem (
        { pkgs }:
        let
          extra-path = with pkgs; [
            dotnetCorePackages.sdk_11_0-bin
            dotnet-sdk_11
            mono
            msbuild
            omnisharp-roslyn

            # RISC-V bare-metal toolchain
            pkgsCross.riscv32-embedded.buildPackages.gcc
            pkgsCross.riscv32-embedded.buildPackages.binutils

            # RISC-V ISA reference simulator for lock-step co-simulation
            spike
            dtc # device tree compiler, required by spike
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
        }
      );
    };
}
