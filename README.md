# Neuropil

A fruit fly brain where every neuron is an actor and every spike is a message.

The wiring is the [FlyWire](https://flywire.ai) connectome: 138,639 neurons and 54.5 million synapses. The neuron model is from [Shiu et al. (Nature, 2024)](https://doi.org/10.1038/s41586-024-07763-9), who showed that a simple model on this wiring predicts how a fly decides to feed or groom. Neuropil rebuilds that model in C# with Akka.NET.

Stimulate the sugar-sensing neurons and the signal travels from the fly's taste neurons to the motor neurons that make it feed. The actor version spikes identically to a plain array simulator run on the same brain.
