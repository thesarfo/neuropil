# Neuropil

A fruit fly brain where every neuron is an actor and every spike is a message.

The wiring comes from [FlyWire](https://flywire.ai): 138,639 neurons and 54.5 million synapses. The neuron model comes from [Shiu et al. (Nature, 2024)](https://doi.org/10.1038/s41586-024-07763-9), who showed that a simple model running on this wiring predicts how a fly decides to feed or groom. Neuropil rebuilds that model in C# with Akka.NET. The goal is a browser demo: drop sugar near a fly and watch the signal travel from its taste neurons to the motor neurons that make it feed.

## Status

Early. The simulator works and matches the paper, but it only runs from the command line so far. Next up is a browser view where you can watch neurons fire, then the whole brain split into one actor per brain region, then the fly.

## Does it match the paper?

Yes. Neuropil has two simulators: a plain array version as a baseline, and the actor version. With the random input turned off, the array version produces the same 12,809 spikes as the [original Brian2 model](https://github.com/philshiu/Drosophila_brain_model) over 50 ms of the whole brain. In the sugar experiment, firing rates correlate with the reference at r = 0.9997, and the proboscis motor neuron MN9 fires at 78.5 Hz against the reference's 77.7 Hz.

The actor version matches the array version exactly: 13,321 identical spikes on a 373-neuron sugar circuit.

## How the actors keep time

Actors don't share a clock, but a neuron can't move forward until every spike headed its way has arrived. The model gives us a way out: spikes always take 1.8 ms (18 steps) to land. So a clock actor hands out time in 18-step chunks. Each neuron runs its chunk alone, sends its spikes, waits for them to be acknowledged, and reports back. Once every neuron has reported, the next chunk starts. Neurons at rest sit out until a spike wakes them.

## A note for anyone reimplementing the model

It isn't a plain integrate-and-fire neuron. A spike adds to a synaptic input that decays over 5 ms, and that input drives the voltage. And after a neuron fires, Brian2 throws away every spike that reaches it during the 2.2 ms refractory period. If you hold on to them instead, downstream neurons fire about twice as fast as they should.

## Credits

Connectome: FlyWire FAFB v783 (CC BY 4.0). Model: Shiu, P.K. et al. "A Drosophila computational brain model reveals sensorimotor processing." Nature 634, 210–219 (2024), with code from [philshiu/Drosophila_brain_model](https://github.com/philshiu/Drosophila_brain_model) (MIT).
