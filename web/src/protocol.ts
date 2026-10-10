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
};

export type ServerMessage =
  | { type: "hello"; circuits: CircuitSummary[] }
  | CircuitMessage
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
  | { type: "watch"; neuron: number | null };
