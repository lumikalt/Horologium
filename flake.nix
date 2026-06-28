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
        in
        {
          inherit (t) softfloat sparta olympia;
          default = t.olympia;
        }
      );

      devShells = forEachSupportedSystem (
        { pkgs }:
        let
          olympia = (olympiaToolchain pkgs).olympia;

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

            # Olympia timing model (`olympia` on PATH), built by the flake — see
            # packages.olympia / nix/olympia.nix. Feed it a Horologium trace:
            #   dotnet run --project Runner -- <elf> --trace-json trace.json
            #   olympia trace.json
            olympia
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
