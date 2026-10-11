import type { BrainMessage, BrainMeta } from "./protocol";
import type { Subject, Tone } from "./subject";

/** The whole brain: a position, region and type per neuron. Connections stay on the server. */
export class BrainModel implements Subject {
  readonly name: string;
  readonly dtMs: number;
  readonly count: number;
  readonly actors: number;
  readonly labels: Map<number, string>;
  readonly stimuli: Subject["stimuli"];
  readonly x: Float32Array;
  readonly y: Float32Array;

  readonly rasterRows: number;
  readonly rasterDividers: number[] = [];
  readonly rasterCaption = "one row per brain region";
  readonly groups: { names: string[]; of(neuron: number): number };

  private readonly sensory = new Set<number>();

  constructor(private readonly message: BrainMessage, private readonly meta: BrainMeta) {
    this.name = message.name;
    this.dtMs = message.dtMs;
    this.count = message.neurons;
    this.actors = message.actors;
    this.labels = new Map(message.labels.map((l) => [l.neuron, l.label]));
    this.stimuli = message.stimuli;
    for (const stimulus of message.stimuli) for (const neuron of stimulus.sensory) this.sensory.add(neuron);
    this.x = Float32Array.from(meta.x);
    this.y = Float32Array.from(meta.y);
    this.rasterRows = meta.regions.length;
    this.groups = { names: meta.regions.map((r) => r || "(none)"), of: (neuron) => meta.region[neuron] };
  }

  nameOf(neuron: number): string {
    return this.labels.get(neuron) ?? (this.meta.types[this.meta.type[neuron]] || `#${neuron}`);
  }

  tone(neuron: number): Tone {
    if (this.labels.has(neuron)) return "output";
    if (this.sensory.has(neuron)) return "sensory";
    return this.message.inhibitory[neuron] === "1" ? "inhibitory" : "excitatory";
  }

  facts(neuron: number): [string, string][] {
    const { meta } = this;
    return [
      ["Kind", this.sensory.has(neuron) ? "sensory" : this.message.inhibitory[neuron] === "1" ? "inhibitory" : "excitatory"],
      ["Cell type", meta.types[meta.type[neuron]] || "unnamed"],
      ["Class", meta.classes[meta.class[neuron]] || "unknown"],
      ["Region", meta.regions[meta.region[neuron]] || "none"],
    ];
  }

  neighbours(): null {
    return null;
  }

  rasterRow(neuron: number): number {
    return this.meta.region[neuron];
  }
}
