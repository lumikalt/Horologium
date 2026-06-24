# To-Do

## Face

- [ ] Caching/memoization to Face's execution so we don't constantly reevaluate the same instruction results.
- [ ] L2 and L3 caches.
- [ ] Assembler to simulate RISC-V in-place.
- [ ] Cache and virtual addressing visualization.

## RISC-V

### Extensions

- [ ] Continue extending the V extension.
- [ ] Supervisor and user-privileged execution.

## Mechanism

- [ ] Generic interfaces for external devices.
- [ ] Cache pre-fetching.

### Out-of-Order Execution

- [ ] Improve definition of generic units with latency at producing outputs.
- [ ] Streaming-Engine to allow for UVE.

### Branch Prediction

- [x] Hashed Perceptron / Path-based Perceptron
- [x] ITTAGE (Indirect Branch Target Predictor)
- [x] BATAGE (Bimodal-Augmented TAGE)
- [ ] LLBP: https://ieeexplore.ieee.org/abstract/document/11408567/
- [ ] VLA-TAGE: https://ieeexplore.ieee.org/document/11417886
- [ ] Branch pre-computation: https://hps.ece.utexas.edu/pub/TEA.pdf
