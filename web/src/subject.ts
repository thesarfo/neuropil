import type { CircuitInfo } from "./protocol";

export type Edge = { other: number; weight: number };
export type Tone = "sensory" | "excitatory" | "inhibitory" | "output";

/** What the controls, inspector and spike plot need to know, whether it's a small circuit or the whole brain. */
export interface Subject {
  readonly name: string;
  readonly dtMs: number;
  readonly count: number;
  readonly labels: ReadonlyMap<number, string>;
  /** Ways to stimulate it. Sensory is null when the server already knows the group. */
  readonly stimuli: { name: string; info: CircuitInfo; sensory: number[] | null }[];

  nameOf(neuron: number): string;
  tone(neuron: number): Tone;
  /** Rows of the inspector: label and value. */
  facts(neuron: number): [string, string][];
  /** Null when the connections have to be asked for. */
  neighbours(neuron: number): { id: string; inputs: Edge[]; outputs: Edge[] } | null;

  readonly rasterRows: number;
  readonly rasterDividers: number[];
  readonly rasterCaption: string;
  rasterRow(neuron: number): number;

  /** Coarser grouping to summarise activity by, if there is one. */
  readonly groups: { names: string[]; of(neuron: number): number } | null;
}
