# Olympia (riscv-software-src/riscv-perf-model), the Sparta-based RISC-V
# performance model used for trace-driven *timing* co-simulation against
# Horologium's OoO train. Feed it a `Runner --trace-json` trace:
#
#   dotnet run --project src/Apps/Runner -- <elf> --trace-json trace.json
#   olympia trace.json
#
# The build is sandbox-clean: softfloat is pre-built (nix/softfloat.nix) and
# stubbed in (no ExternalProject git-clone), and bitset2's FetchContent is
# redirected to a prefetched source.
{
  stdenv,
  fetchFromGitHub,
  writeText,
  cmake,
  flex,
  bison,
  pkg-config,
  git,
  makeWrapper,
  boost,
  yaml-cpp,
  rapidjson,
  sqlite,
  hdf5-cpp,
  zlib,
  zstd,
  sparta,
  softfloat,
}:

let
  # Submodules are fetched individually and assembled in postUnpack rather than
  # via fetchSubmodules, because the (unused) nested fsl/modules/mavis submodule
  # has an SSH URL the sandbox can't clone. The build redirects fsl to the top
  # mavis (FSL_MAVIS_PATH), so fsl/modules/mavis is left empty.
  mavisSrc = fetchFromGitHub {
    owner = "riscv-software-src";
    repo = "riscv-mirror-mavis";
    rev = "a1bb71a0714e6b6784b1399dc2a0a3183189f1d4";
    hash = "sha256-vvq74Y3S1sdbEbsiTTEoVYkgKDxZgC5/d+GY5uv7roY=";
  };
  elfioSrc = fetchFromGitHub {
    owner = "serge1";
    repo = "ELFIO";
    rev = "7a1de75522f0d1e42a68d206c8165b45327ac16e";
    hash = "sha256-l+tKuriQZJioEba81E+AFGekGx4paZ969DS4AnGJ3RI=";
  };
  stfSrc = fetchFromGitHub {
    owner = "riscv-software-src";
    repo = "riscv-mirror-stf-library";
    rev = "a96e73fc339499fb662f9da97319cd4ff98b7578";
    hash = "sha256-RoFUiBIMwqGESgJiZgW7ybCIfeFdrySNC3aacq1bJ00=";
  };
  fslSrc = fetchFromGitHub {
    owner = "riscv-software-src";
    repo = "riscv-mirror-fsl";
    rev = "8ae02fa607b4cacb454e2ca86461f356703bc950";
    hash = "sha256-8ew8xV4KJXNz7Iosc0pywsEDqB04Rkb61pG+r+15I3A=";
  };

  # bitset2 (header-only) is pulled by mavis via FetchContent; point it at a
  # prefetched source rather than letting it git-clone in the sandbox.
  bitset2 = fetchFromGitHub {
    owner = "ClaasBontus";
    repo = "bitset2";
    rev = "084d7c578100b9086615fb7e742e3866c6dd2598";
    hash = "sha256-Qe8rvasCf2tmdkJDcXQkVhbDjWoel2TOqYammLIKGZw=";
  };

  # Replacement for mavis/cmake/softfloat.cmake: define the same `softfloat`
  # INTERFACE target against the prebuilt derivation instead of an ExternalProject
  # git-clone + in-source build.
  softfloatStub = writeText "softfloat.cmake" ''
    add_library(softfloat INTERFACE)
    target_link_libraries(softfloat INTERFACE ${softfloat}/build/Linux-x86_64-GCC/softfloat.a)
    target_compile_definitions(softfloat INTERFACE SOFTFLOAT_ROUND_ODD INLINE_LEVEL=5 SOFTFLOAT_FAST_DIV32TO16 SOFTFLOAT_FAST_DIV64TO32 SOFTFLOAT_FAST_INT64 LITTLEENDIAN=1 INLINE=inline SOFTFLOAT_BUILTIN_CLZ=1 SOFTFLOAT_INTRINSIC_INT128=1)
    target_include_directories(softfloat SYSTEM INTERFACE ${softfloat}/source/RISCV ${softfloat}/source/include ${softfloat}/build/Linux-x86_64-GCC)
  '';
in
stdenv.mkDerivation {
  pname = "olympia";
  version = "unstable-2024-1ad3dd1";

  src = fetchFromGitHub {
    owner = "riscv-software-src";
    repo = "riscv-perf-model";
    rev = "1ad3dd1e652ff0569d881f13b7cd8044243d4251";
    hash = "sha256-GCtYVBfdREn6tJU9274+nWv8JqsYBLM1+RJrWXnBeeg=";
  };

  # Assemble the submodules (see mavisSrc/etc. above) into their gitlink paths.
  postUnpack = ''
    pushd "$sourceRoot" >/dev/null
    rm -rf mavis stf_lib fsl
    # cp -a (not -r --no-preserve=mode): keep file modes so scripts like
    # stf_lib's gen_git_version.sh stay executable. --no-preserve=ownership
    # avoids a non-root ownership error; chmod -R u+w restores writability.
    cp -a --no-preserve=ownership ${mavisSrc} mavis
    chmod -R u+w mavis                                # cp -a copies read-only; make writable before editing
    rm -rf mavis/elfio                                # mirror ships an empty elfio/ placeholder
    cp -a --no-preserve=ownership ${elfioSrc} mavis/elfio
    cp -a --no-preserve=ownership ${stfSrc}   stf_lib
    cp -a --no-preserve=ownership ${fslSrc}   fsl
    chmod -R u+w mavis stf_lib fsl
    # core's git_version_target DEPENDS on .git/{HEAD,index}; stub them so make
    # can satisfy the dependency. GenerateGitVersion.cmake falls back to "unknown"
    # when git can't describe, so no real repo is needed. This top-level .git does
    # not affect mavis (which checks mavis/.git).
    mkdir -p .git && : > .git/HEAD && : > .git/index
    popd >/dev/null
  '';

  nativeBuildInputs = [
    cmake
    flex # fsl (Fusion) lexer
    bison # fsl (Fusion) parser
    pkg-config # stf_lib finds libzstd via pkg-config
    git # mavis test/fp's simde ExternalProject checks for git at configure time
    makeWrapper
  ];

  buildInputs = [
    boost
    yaml-cpp
    rapidjson
    sqlite
    hdf5-cpp
    zlib
    zstd
    sparta
    softfloat
  ];

  postPatch = ''
    patchShebangs .                                  # stf_lib gen_git_version.sh has a /bin/bash shebang
    cp ${softfloatStub} mavis/cmake/softfloat.cmake  # avoid the softfloat ExternalProject git-clone
  '';

  cmakeFlags = [
    # fastdebug, not Release: Release enables -flto, which trips a GCC 15 LTO link
    # bug (dropped CPUFactory symbols). fastdebug is optimized without LTO; the
    # timing *stats* are identical.
    "-DCMAKE_BUILD_TYPE=fastdebug"
    "-DSPARTA_SEARCH_DIR=${sparta}"
    "-DCMAKE_POLICY_VERSION_MINIMUM=3.5"
    "-DCMAKE_CXX_FLAGS=-Wno-error"
    "-DFETCHCONTENT_SOURCE_DIR_BITSET2=${bitset2}"
  ];

  buildPhase = ''
    runHook preBuild
    jobs=''${NIX_BUILD_CORES:-4}; [ "$jobs" -gt 4 ] && jobs=4
    cmake --build . --target olympia -j"$jobs"
    runHook postBuild
  '';

  # Olympia resolves mavis ISA JSON + arch configs at runtime via Sparta
  # parameters that default to CWD-relative paths. Install the data and wrap the
  # binary to point those parameters at the store, so `olympia` works from any
  # directory.
  installPhase = ''
    runHook preInstall
    mkdir -p $out/libexec $out/share/olympia
    cp olympia $out/libexec/olympia
    cp -r ../mavis/json $out/share/olympia/mavis_isa_files
    cp -r ../arches $out/share/olympia/arches
    makeWrapper $out/libexec/olympia $out/bin/olympia \
      --add-flags "--arch-search-dir $out/share/olympia/arches" \
      --add-flags "-p top.cpu.core0.mavis.params.isa_file_path $out/share/olympia/mavis_isa_files" \
      --add-flags "-p top.cpu.core0.mavis.params.uarch_file_path $out/share/olympia/arches/isa_json"
    runHook postInstall
  '';

  meta.mainProgram = "olympia";
}
