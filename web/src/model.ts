import type { CircuitMessage } from "./protocol";

export type Edge = { other: number; weight: number };

/** The circuit as the views need it: who connects to whom, and which layer each neuron sits in. */
export class CircuitModel {
  readonly name: string;
  readonly dtMs: number;
  readonly count: number;
  readonly ids: string[];
  readonly sensory: boolean[];
  readonly labels: (string | null)[];
  readonly inhibitory: boolean[];
  readonly outputs: Edge[][];
  readonly inputs: Edge[][];
  readonly edges: CircuitMessage["edges"];
  /** Excitatory hops from the sensory neurons. Neurons nothing excites go in the last layer. */
  readonly layer: number[];
  readonly layers: number[][];
  /** Index of the layer holding neurons no excitatory path reaches, if there is one. */
  readonly unreachedLayer: number | null;

  constructor(message: CircuitMessage) {
    this.name = message.name;
    this.dtMs = message.dtMs;
    this.count = message.neurons.length;
    this.ids = message.neurons.map((n) => n.id);
    this.sensory = message.neurons.map((n) => n.sensory);
    this.labels = message.neurons.map((n) => n.label);
    this.edges = message.edges;

    this.outputs = Array.from({ length: this.count }, () => []);
    this.inputs = Array.from({ length: this.count }, () => []);
    const outgoing = new Array<number>(this.count).fill(0);
    for (const [pre, post, weight] of message.edges) {
      this.outputs[pre].push({ other: post, weight });
      this.inputs[post].push({ other: pre, weight });
      outgoing[pre] += weight;
    }
    this.inhibitory = outgoing.map((sum) => sum < 0);
    for (const list of [...this.outputs, ...this.inputs]) list.sort((a, b) => Math.abs(b.weight) - Math.abs(a.weight));

    this.layer = new Array<number>(this.count).fill(-1);
    let frontier: number[] = [];
    this.sensory.forEach((isSensory, i) => {
      if (isSensory) {
        this.layer[i] = 0;
        frontier.push(i);
      }
    });
    let depth = 0;
    while (frontier.length > 0) {
      const next: number[] = [];
      for (const pre of frontier) {
        for (const { other, weight } of this.outputs[pre]) {
          if (weight > 0 && this.layer[other] < 0) {
            this.layer[other] = depth + 1;
            next.push(other);
          }
        }
      }
      frontier = next;
      depth++;
    }
    const last = Math.max(0, ...this.layer);
    const unreached = this.layer.some((l) => l < 0);
    this.unreachedLayer = unreached ? last + 1 : null;
    this.layers = Array.from({ length: last + (unreached ? 2 : 1) }, () => []);
    this.layer.forEach((l, i) => {
      if (l < 0) this.layer[i] = last + 1;
      this.layers[this.layer[i]].push(i);
    });
  }

  nameOf(neuron: number): string {
    return this.labels[neuron] ?? `#${neuron}`;
  }

  kind(neuron: number): string {
    if (this.sensory[neuron]) return "sensory";
    return this.inhibitory[neuron] ? "inhibitory" : "excitatory";
  }
}
