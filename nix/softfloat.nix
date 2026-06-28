# Berkeley SoftFloat, built exactly as mavis/cmake/softfloat.cmake's
# ExternalProject would (RISCV specialization + its -D opts), but as a standalone
# derivation so Olympia's sandboxed build needs no network and no in-source build
# of a read-only store path. See nix/olympia.nix for how it is consumed.
{ stdenv, fetchFromGitHub }:

stdenv.mkDerivation {
  pname = "berkeley-softfloat-3";
  version = "unstable-2021-08-12";

  src = fetchFromGitHub {
    owner = "ucb-bar";
    repo = "berkeley-softfloat-3";
    rev = "a0c6494cdc11865811dec815d5c0049fba9d82a8";
    hash = "sha256-TO1DhvUMd2iP5gvY9Hqy9Oas0Da7lD0oRVPBlfAzc90=";
  };

  buildPhase = ''
    runHook preBuild
    jobs=''${NIX_BUILD_CORES:-4}; [ "$jobs" -gt 4 ] && jobs=4
    make -C build/Linux-x86_64-GCC -j"$jobs" \
      SPECIALIZE_TYPE=RISCV \
      SOFTFLOAT_OPTS="-DSOFTFLOAT_ROUND_ODD -DINLINE_LEVEL=5 -DSOFTFLOAT_FAST_DIV32TO16 -DSOFTFLOAT_FAST_DIV64TO32 -fPIC"
    runHook postBuild
  '';

  # Keep the source + build tree so the consumer's stub cmake can point its
  # include dirs (source/RISCV, source/include, build/Linux-x86_64-GCC) and the
  # softfloat.a at the store.
  installPhase = ''
    runHook preInstall
    mkdir -p $out
    cp -r source build $out/
    runHook postInstall
  '';
}
