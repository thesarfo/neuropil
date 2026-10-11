import type { CircuitInfo, CircuitMessage } from "./protocol";
import type { Edge, Subject, Tone } from "./subject";

/** A small circuit: who connects to whom, and which layer each neuron sits in. */
export class CircuitModel implements Subject {
  readonly name: string;
  readonly info: CircuitInfo;
  readonly dtMs: number;
  readonly count: number;
  readonly ids: string[];
  readonly sensory: boolean[];
  readonly labels: Map<number, string>;
  readonly inhibitory: boolean[];
  readonly outputs: Edge[][];
  readonly inputs: Edge[][];
  readonly edges: CircuitMessage["edges"];
  /** Excitatory hops from the sensory neurons. Neurons nothing excites go in the last layer. */
  readonly layer: number[];
  readonly layers: number[][];
  /** Index of the layer holding neurons no excitatory path reaches, if there is one. */
  readonly unreachedLayer: number | null;

  readonly stimuli: Subject["stimuli"];
  readonly rasterRows: number;
  readonly rasterDividers: number[];
  readonly rasterCaption = "one row per neuron, sensors at the top";
  readonly groups = null;
  private readonly row: Int32Array;

  constructor(message: CircuitMessage) {
    this.name = message.name;
    this.info = message.info;
    this.dtMs = message.dtMs;
    this.count = message.neurons.length;
    this.ids = message.neurons.map((n) => n.id);
    this.sensory = message.neurons.map((n) => n.sensory);
    this.labels = new Map(message.neurons.flatMap((n, i) => (n.label ? [[i, n.label] as [number, string]] : [])));
    this.edges = message.edges;
    this.stimuli = [{ name: message.name, info: message.info, sensory: null }];

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

    this.rasterRows = this.count;
    this.row = new Int32Array(this.count);
    this.layers.flat().forEach((neuron, position) => (this.row[neuron] = position));
    this.rasterDividers = [];
    let rows = 0;
    for (const layer of this.layers.slice(0, -1)) this.rasterDividers.push((rows += layer.length));
  }

  nameOf(neuron: number): string {
    return this.labels.get(neuron) ?? `#${neuron}`;
  }

  tone(neuron: number): Tone {
    if (this.labels.has(neuron)) return "output";
    if (this.sensory[neuron]) return "sensory";
    return this.inhibitory[neuron] ? "inhibitory" : "excitatory";
  }

  layerName(layer: number): string {
    return layer === 0 ? "sensors" : layer === this.unreachedLayer ? "other" : `hop ${layer}`;
  }

  facts(neuron: number): [string, string][] {
    return [
      ["Kind", this.sensory[neuron] ? "sensory" : this.inhibitory[neuron] ? "inhibitory" : "excitatory"],
      ["Layer", this.layerName(this.layer[neuron])],
      ["Inputs", String(this.inputs[neuron].length)],
      ["Outputs", String(this.outputs[neuron].length)],
    ];
  }

  neighbours(neuron: number) {
    return { id: this.ids[neuron], inputs: this.inputs[neuron], outputs: this.outputs[neuron] };
  }

  rasterRow(neuron: number): number {
    return this.row[neuron];
  }
}
