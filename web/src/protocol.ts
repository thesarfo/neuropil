// Messages exchanged with the server. Keep in step with ClientMessage.cs and Connection.cs.

export type CircuitSummary = { name: string; title: string; neurons: number; edges: number };

export type CircuitInfo = { title: string; start: string; stop: string; about: string };

export type CircuitMessage = {
  type: "circuit";
  name: string;
  info: CircuitInfo;
  dtMs: number;
  neurons: { id: string; sensory: boolean; label: string | null }[];
  edges: [pre: number, post: number, weight: number][];
};

export type BrainMessage = {
  type: "brain";
  name: string;
  /** Present for the embodied fly: the sensors it accepts and the motor groups it reports. */
  body: { sensors: string[]; motors: string[]; periodMs: number } | null;
  dtMs: number;
  neurons: number;
  actors: number;
  /** One character per neuron: "1" if inhibitory. */
  inhibitory: string;
  labels: { neuron: number; label: string }[];
  stimuli: { name: string; info: CircuitInfo; sensory: number[] }[];
};

/** /api/brain/meta: parallel arrays in neuron order, strings interned into tables. */
export type BrainMeta = {
  neurons: number;
  regions: string[];
  classes: string[];
  types: string[];
  region: number[];
  class: number[];
  type: number[];
  x: number[];
  y: number[];
  z: number[];
};

export type NeighboursMessage = {
  type: "neighbours";
  neuron: number;
  id: string;
  inputs: [neuron: number, weight: number][];
  outputs: [neuron: number, weight: number][];
};

export type FrameMessage = {
  type: "frame";
  step: number;
  timeMs: number;
  running: boolean;
  speed: number | null;
  spikes: [step: number, neuron: number][];
  rates: [neuron: number, hz: number][];
  truncated: boolean;
  trace: { neuron: number; fromStep: number; mv: number[] } | null;
  /** Firing rate per motor group over the last 50 ms; embodied fly only. */
  motor: Record<string, number> | null;
};

export type ServerMessage =
  | { type: "hello"; circuits: CircuitSummary[]; brain: { name: string; neurons: number; edges: number; fly: string | null } | null }
  | CircuitMessage
  | BrainMessage
  | NeighboursMessage
  | FrameMessage
  | { type: "error"; message: string };

export type ClientMessage =
  | { type: "load"; circuit: string }
  | { type: "stimulate"; rateHz: number; neurons?: number[] }
  | { type: "play" }
  | { type: "pause" }
  | { type: "reset" }
  | { type: "step"; steps: number }
  | { type: "speed"; value: number | "max" }
  | { type: "watch"; neuron: number | null }
  | { type: "neighbours"; neuron: number }
  | { type: "sense"; levels: Record<string, number> };
