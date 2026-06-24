// Default (weak) machine-mode trap handler for rv32si tests.
// Linked alongside each rv32si test; any test that defines its own
// .global mtvec_handler overrides this weak definition.
// If reached unexpectedly, EBREAK halts the simulator as a fail.

.section .text
.weak mtvec_handler
mtvec_handler:
    ebreak
