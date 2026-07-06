# gem5 RISCV build — provides `gem5` binary with TraceCPU + protobuf support.
# Used for end-to-end validation of Horologium's HELF elastic trace pipeline:
#   dotnet run --project Runner -- <elf> --elastic-record out.helf
#   dotnet run --project Runner -- --elastic-to-gem5 out.helf out.gem5data
#   dotnet run --project Runner -- --fetch-to-gem5   out.helf out.gem5fetch
#   gem5 gem5-scripts/trace_cpu_riscv.py \
#       --data-trace-file out.gem5data --inst-trace-file out.gem5fetch
#
# gem5 vendored all C/C++ deps in ext/ (no git submodules), so the nix sandbox
# build is straightforward: SCons + Python + protobuf + zlib.
# Caps parallel jobs at 8 to keep memory use under ~16 GB (gem5's C++ unity
# build is memory-hungry; full parallelism on a 12-core machine OOMs).
{
  stdenv,
  fetchFromGitHub,
  scons,
  python3,
  pkg-config,
  m4,
  protobuf,
  abseil-cpp,
  zlib,
}:

stdenv.mkDerivation rec {
  pname = "gem5";
  version = "25.1.0.1";

  src = fetchFromGitHub {
    owner = "gem5";
    repo = "gem5";
    rev = "c8222cc67a399bfc01e8658dd14b30d5bfd634f9";
    hash = "sha256-miL4VC3M/w2bi46AG8YXgsz8duzPuJRzjN44BDaebF0=";
  };

  nativeBuildInputs = [
    scons
    python3
    pkg-config
    m4
    protobuf # provides protoc for .proto → C++ generation
  ];

  buildInputs = [
    protobuf
    abseil-cpp # protobuf 34+ links abseil transitively; nix strict linker needs it explicit
    zlib
  ];

  postPatch = ''
    patchShebangs build_tools/kconfig_base.py ext/Kconfiglib/defconfig.py util/cpt_upgrader.py
  '';

  buildPhase = ''
    runHook preBuild
    # protobuf 34+ depends on abseil transitively; nix's strict linker rejects
    # DSOs that resolve symbols but aren't on the command line.
    # NIX_LDFLAGS_BEFORE entries land in ld's extraBefore (before -lprotobuf)
    # so --copy-dt-needed-entries is processed before the library flags.
    export NIX_LDFLAGS_BEFORE="--copy-dt-needed-entries $NIX_LDFLAGS_BEFORE"
    jobs=''${NIX_BUILD_CORES:-4}; [ "$jobs" -gt 8 ] && jobs=8
    scons build/RISCV/gem5.opt -j"$jobs"
    runHook postBuild
  '';

  installPhase = ''
    runHook preInstall
    mkdir -p $out/bin
    cp build/RISCV/gem5.opt $out/bin/gem5
    runHook postInstall
  '';

  meta.mainProgram = "gem5";
}
