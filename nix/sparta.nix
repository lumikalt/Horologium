# Sparta modeling framework (sparcians/map), the foundation Olympia is built on.
# Builds the `sparta` library only — not the tests or examples, which dominate
# build time and -Werror surface — and installs the static lib + headers + cmake
# config that Olympia's find_package(Sparta) needs.
#
# Toolchain notes (GCC 15 / Boost 1.89 / CMake 4):
#   - CMAKE_POLICY_VERSION_MINIMUM=3.5: CMake 4 dropped pre-3.5 policy compat.
#   - -Wno-error: GCC 15 flags warnings Sparta never saw.
{
  stdenv,
  fetchFromGitHub,
  cmake,
  boost,
  yaml-cpp,
  rapidjson,
  sqlite,
  hdf5-cpp,
  zlib,
  zstd,
}:

stdenv.mkDerivation {
  pname = "sparta";
  version = "map_v2.2.3";

  src = fetchFromGitHub {
    owner = "sparcians";
    repo = "map";
    rev = "map_v2.2.3";
    fetchSubmodules = true; # simdb
    hash = "sha256-zvBKB/T1Ra6JcSRF8m2NpFW7cszSlrEtsBwOxHzyDYQ=";
  };

  # The framework lives in the map/sparta subdirectory.
  setSourceRoot = "sourceRoot=$(echo */sparta)";

  # The build embeds `git describe` and hard-errors if it fails; there is no .git
  # in the fetched source, so stub it with the pinned version.
  postPatch = ''
    substituteInPlace CMakeLists.txt \
      --replace-fail 'git describe --tags --always' 'echo map_v2.2.3'
  '';

  nativeBuildInputs = [ cmake ];
  buildInputs = [
    boost
    yaml-cpp
    rapidjson
    sqlite
    hdf5-cpp
    zlib
    zstd
  ];

  cmakeFlags = [
    "-DCMAKE_BUILD_TYPE=Release"
    "-DCMAKE_POLICY_VERSION_MINIMUM=3.5"
    "-DCMAKE_CXX_FLAGS=-Wno-error"
  ];

  # Cap parallelism: Sparta template units use 1–2 GB each in cc1plus, so an
  # unbounded -j on a many-core/16 GB box OOMs. 4 is a safe ceiling.
  buildPhase = ''
    runHook preBuild
    jobs=''${NIX_BUILD_CORES:-4}; [ "$jobs" -gt 4 ] && jobs=4
    cmake --build . --target sparta -j"$jobs"
    runHook postBuild
  '';

  # cmake --install runs the install rules without rebuilding `all` (which we
  # skipped); the rules only need the built libsparta.a.
  installPhase = ''
    runHook preInstall
    cmake --install . --prefix "$out"
    runHook postInstall
  '';
}
