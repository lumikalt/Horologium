# UVE-extended Spike ISA simulator (AnaBSF/riscv-isa-sim, uve branch).
# UVE (Unlimited Vector Extension) adds streaming-engine instructions
# (ss.*, so.*) to RISC-V; this fork is the reference implementation used to
# verify Horologium's UVE decoder encodings.
#
# Run UVE programs:
#   uve-spike --isa=rv32imafcv_xuve <elf>
{
  stdenv,
  fetchFromGitHub,
  dtc,
  boost,
}:

stdenv.mkDerivation {
  pname = "uve-spike";
  version = "unstable-a048271";

  src = fetchFromGitHub {
    owner = "AnaBSF";
    repo = "riscv-isa-sim";
    rev = "a048271e03682f259dc91179d6c1ac3b819f0578";
    hash = "sha256-ab2hgQxboFSpRT3RwEBBWQn6mjH62WIT1EhAinxy6QM=";
  };

  nativeBuildInputs = [ dtc ];
  buildInputs = [ boost ];

  # softfloat and fesvr are bundled in-tree; no external submodules needed.
  configureFlags = [
    "--prefix=${placeholder "out"}"
    # The AX_BOOST_REGEX macro tries to glob $BOOSTLIBDIR/libboost_regex*.so*
    # which fails in the Nix sandbox; name the library explicitly to skip it.
    "--with-boost-regex=boost_regex"
    # ASIO check uses deprecated io_service API that fails to compile with
    # Boost ≥ 1.74; disable rather than patch.
    "--with-boost-asio=no"
  ];

  enableParallelBuilding = true;

  # Rename the installed `spike` binary to `uve-spike` so it doesn't shadow
  # the nixpkgs spike in the devShell.
  postInstall = ''
    mv $out/bin/spike $out/bin/uve-spike
  '';

  meta.mainProgram = "uve-spike";
}
