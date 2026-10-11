# Validation

Neuropil's array simulator against the Brian2 model from Shiu et al. (2024), on the FlyWire v783 connectome.
Each experiment is 30 trials of one second with the listed sensory neurons driven at 150 Hz.
Regenerate with `uv run python report.py` in `prep/` after running both sides.

| Experiment | Driven neurons | Active in Brian2 | Active in Neuropil | Pearson r | Same top 20 |
|---|---|---|---|---|---|
| antenna | 146 | 633 | 660 | 0.9997 | 20/20 |
| sugarR | 21 | 433 | 447 | 0.9997 | 20/20 |
| waterR | 18 | 261 | 254 | 0.9997 | 20/20 |

Pearson r is over every neuron active in either simulator. The top 20 leaves out the driven neurons,
which all fire near the stimulus rate in an arbitrary order.

## Named output neurons

| Experiment | Neuron | Brian2 (Hz) | Neuropil (Hz) |
|---|---|---|---|
| antenna | aDN1 | 20.0 | 20.1 |
| antenna | aDN2 | 20.6 | 20.5 |
| antenna | aBN1 | 49.8 | 50.0 |
| sugarR | MN9 | 77.7 | 78.5 |
| waterR | MN9 | 5.6 | 6.2 |

With the random input replaced by a deterministic one, the two simulators produce identical spikes:
12,809 of 12,809 over 50 ms of the whole brain. The actor modes are then checked against the array
simulator spike for spike by `neuropil compare` and `neuropil bench`.
