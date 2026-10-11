import "@fontsource/ibm-plex-sans/latin-400.css";
import "@fontsource/ibm-plex-sans/latin-500.css";
import "@fontsource/ibm-plex-sans/latin-600.css";
import "@fontsource/ibm-plex-mono/latin-400.css";
import "@fontsource/ibm-plex-mono/latin-500.css";
import { BrainModel } from "./brainModel";
import { BrainView } from "./brainView";
import { neuronColour, readPalette } from "./canvas";
import { CircuitView } from "./circuitView";
import { FlyWorld, type DropKind } from "./flyWorld";
import { CircuitModel } from "./model";
import type { BrainMessage, BrainMeta, FrameMessage, NeighboursMessage, ServerMessage } from "./protocol";
import { RasterView } from "./rasterView";
import { Connection, type ConnectionState } from "./socket";
import type { Edge, Subject } from "./subject";
import { TraceView } from "./traceView";

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

// A stored choice wins over the system's theme. Applied before any colours are read.
const darkQuery = matchMedia("(prefers-color-scheme: dark)");
function storedTheme(): string | null {
  try {
    return localStorage.getItem("theme");
  } catch {
    return null;
  }
}
if (storedTheme()) document.documentElement.dataset.theme = storedTheme()!;
const isDark = () => (document.documentElement.dataset.theme ?? (darkQuery.matches ? "dark" : "light")) === "dark";

const ui = {
  status: $("status"),
  circuit: $<HTMLSelectElement>("circuit"),
  stimulus: $<HTMLSelectElement>("stimulus"),
  stimulate: $<HTMLButtonElement>("stimulate"),
  rate: $<HTMLInputElement>("rate"),
  rateOut: $("rate-out"),
  play: $<HTMLButtonElement>("play"),
  step: $<HTMLButtonElement>("step"),
  reset: $<HTMLButtonElement>("reset"),
  speed: $<HTMLSelectElement>("speed"),
  clock: $("clock"),
  active: $("active"),
  outputs: $("outputs"),
  empty: $("empty"),
  about: $("about"),
  pin: $<HTMLButtonElement>("pin"),
  unpin: $<HTMLButtonElement>("unpin"),
  window: $("window"),
  compare: $<HTMLTableElement>("compare"),
  pinnedName: $("pinned-name"),
  silencedNote: $("silenced-note"),
  silencedCount: $("silenced-count"),
  restoreAll: $<HTMLButtonElement>("restore-all"),
  circuitCanvas: $<HTMLCanvasElement>("circuit-view"),
  brainCanvas: $<HTMLCanvasElement>("brain-view"),
  flyCanvas: $<HTMLCanvasElement>("fly-view"),
  flyTools: $("fly-tools"),
  tap: $<HTMLButtonElement>("tap"),
  clearDrops: $<HTMLButtonElement>("clear-drops"),
  rasterCaption: $("raster-caption"),
  inspector: $("inspector-body"),
  trace: $<HTMLCanvasElement>("trace-view"),
  top: $("top"),
  regionsHeading: $("regions-heading"),
  regions: $("regions"),
};

let palette = readPalette();
let subject: Subject | null = null;
let brainMeta: Promise<BrainMeta> | null = null;
let running = false;
let stimulating = false;
let step = 0;
let selected: number | null = null;
let rates = new Map<number, number>();
let lastListUpdate = 0;
let silenced = new Set<number>();

// Spike counts since the stimulus last changed, for average rates steadier than the 100 ms window.
let counts = new Float64Array(0);
let countFromStep = 0;
let countsIncomplete = false;
let pinned: { name: string; rates: Float64Array } | null = null;

const circuitView = new CircuitView(ui.circuitCanvas, palette, select);
const brainView = new BrainView(ui.brainCanvas, palette, select);
const rasterView = new RasterView($<HTMLCanvasElement>("raster-view"), palette);
const traceView = new TraceView(ui.trace, palette);
let stage: CircuitView | BrainView = circuitView;

// The embodied fly: its world, and what it last told the brain it could feel.
const world = new FlyWorld(ui.flyCanvas, palette);
let embodied = false;
let lastTimeMs = 0;
let sentSenses = "";

const connection = new Connection(onMessage, onConnectionState);

function onConnectionState(state: ConnectionState): void {
  ui.status.dataset.state = state;
  if (state === "connecting") ui.status.textContent = "Connecting…";
  if (state === "closed") {
    ui.status.textContent = "Disconnected. Retrying…";
    setEnabled(false);
  }
}

function onMessage(message: ServerMessage): void {
  switch (message.type) {
    case "hello": {
      const options = message.circuits.map((c) => new Option(`${c.title} (${c.neurons.toLocaleString()} neurons)`, c.name));
      if (message.brain) options.push(new Option(`Whole brain (${message.brain.neurons.toLocaleString()} neurons)`, message.brain.name));
      if (message.brain?.fly) options.push(new Option("Embodied fly (whole brain)", message.brain.fly));
      ui.circuit.replaceChildren(...options);
      ui.status.textContent = "Connected";
      if (options.length === 0) {
        showEmpty("No circuits found.\nBuild one with `neuropil circuit` and put it in data/circuits/.");
        return;
      }
      load(subject?.name ?? options[0].value);
      break;
    }

    case "circuit": {
      const model = new CircuitModel(message);
      circuitView.setModel(model);
      show(model, circuitView, `${model.count} neurons, ${model.edges.length.toLocaleString()} connections`);
      break;
    }

    case "brain":
      void showBrain(message);
      break;

    case "neighbours":
      if (message.neuron === selected) renderNeighbours(message);
      break;

    case "frame":
      onFrame(message);
      break;

    case "error":
      ui.status.textContent = message.message;
      ui.status.dataset.state = "closed";
      break;
  }
}

function load(name: string): void {
  setEnabled(false);
  ui.status.textContent = "Loading…";
  connection.send({ type: "load", circuit: name });
}

async function showBrain(message: BrainMessage): Promise<void> {
  ui.status.textContent = "Loading neuron positions…";
  brainMeta ??= fetch("/api/brain/meta").then((response) => response.json() as Promise<BrainMeta>);
  const model = new BrainModel(message, await brainMeta);
  brainView.setModel(model);
  show(model, brainView, `${model.count.toLocaleString()} neurons in ${model.actors} region actors`, message.body !== null);
}

/** Switches the page to a newly loaded subject. The server session starts paused and unstimulated. */
function show(next: Subject, view: CircuitView | BrainView, status: string, withBody = false): void {
  subject = next;
  stage = view;
  embodied = withBody;
  ui.flyCanvas.hidden = ui.flyTools.hidden = !withBody;
  ui.stimulate.parentElement!.hidden = withBody;
  world.reset();
  lastTimeMs = 0;
  sentSenses = "";
  if (withBody) ui.speed.value = "0.2";
  ui.circuitCanvas.hidden = view !== circuitView;
  ui.brainCanvas.hidden = view !== brainView;
  ui.circuit.value = next.name;
  ui.empty.hidden = true;
  ui.status.textContent = status;
  ui.status.dataset.state = "open";

  stimulating = false;
  running = false;
  rates = new Map();
  step = 0;
  counts = new Float64Array(next.count);
  pinned = null;
  restartAverage();
  silenced = new Set();
  renderSilenced();

  ui.stimulus.replaceChildren(...next.stimuli.map((s) => new Option(s.info.title, s.name)));
  ui.stimulus.parentElement!.hidden = next.stimuli.length < 2 || withBody;
  ui.rasterCaption.textContent = `· ${next.rasterCaption}`;
  ui.regionsHeading.hidden = ui.regions.hidden = next.groups === null;

  rasterView.setSubject(next);
  traceView.clear(next.dtMs);
  select(null);
  buildOutputs();
  renderStimulus();
  renderCompare();
  renderTop();
  setEnabled(true);
  connection.send({ type: "speed", value: speedValue() });
}

function onFrame(frame: FrameMessage): void {
  if (!subject) return;
  if (frame.step < step) {
    stage.clear();
    rasterView.clear();
    traceView.clear();
    step = frame.step;
    restartAverage();
    world.reset();
    lastTimeMs = 0;
    sentSenses = "";
  }
  step = frame.step;
  running = frame.running;
  if (frame.truncated) countsIncomplete = true;
  for (const [at, neuron] of frame.spikes) {
    stage.spike(neuron);
    counts[neuron]++;
    rasterView.add(at, neuron);
  }
  rates = new Map(frame.rates);
  brainView.setRates(rates);
  if (frame.trace && frame.trace.neuron === selected) traceView.add(frame.trace.fromStep, frame.trace.mv);

  if (embodied) {
    world.advance(frame.timeMs - lastTimeMs, frame.motor ?? {});
    lastTimeMs = frame.timeMs;
    sendSenses();
  }

  ui.clock.textContent = `${frame.timeMs.toFixed(1)} ms`;
  ui.active.textContent = String(rates.size);
  ui.play.textContent = running ? "Pause" : "Play";
  ui.step.disabled = running;
  updateOutputs();

  const now = performance.now();
  if (now - lastListUpdate > 300) {
    lastListUpdate = now;
    renderTop();
    renderSelectedRate();
    renderCompare();
  }
}

function setEnabled(enabled: boolean): void {
  for (const control of [ui.circuit, ui.stimulus, ui.stimulate, ui.rate, ui.play, ui.step, ui.reset, ui.speed, ui.pin]) control.disabled = !enabled;
}

function showEmpty(text: string): void {
  ui.empty.textContent = text;
  ui.empty.hidden = false;
}

function speedValue(): number | "max" {
  return ui.speed.value === "max" ? "max" : Number(ui.speed.value);
}

function stimulus() {
  return subject?.stimuli.find((s) => s.name === ui.stimulus.value) ?? subject?.stimuli[0];
}

function sendSenses(): void {
  const levels = world.sense();
  const key = JSON.stringify(levels);
  if (key === sentSenses) return;
  sentSenses = key;
  connection.send({ type: "sense", levels });
}

function renderStimulus(): void {
  const info = stimulus()?.info;
  ui.about.textContent = embodied
    ? "Click the arena to drop food, or tap an antenna. Feeding and grooming come from the simulated brain. Walking is hand-written, because the connectome stops at the neck."
    : (info?.about ?? "");
  ui.stimulate.setAttribute("aria-pressed", String(stimulating));
  ui.stimulate.textContent = (stimulating ? info?.stop : info?.start) ?? "Stimulate";
}

function sendStimulus(rateHz: number): void {
  const sensory = stimulus()?.sensory;
  connection.send(sensory ? { type: "stimulate", rateHz, neurons: sensory } : { type: "stimulate", rateHz });
}

/** Cuts a neuron's outputs, or restores them. It still fires; nothing downstream hears it. */
function setSilenced(neurons: number[], on: boolean): void {
  for (const neuron of neurons) {
    if (on) silenced.add(neuron);
    else silenced.delete(neuron);
  }
  connection.send({ type: "silence", neurons, silenced: on });
  restartAverage();
  renderSilenced();
}

function renderSilenced(): void {
  circuitView.setSilenced(silenced);
  brainView.setSilenced(silenced);
  ui.silencedNote.hidden = silenced.size === 0;
  ui.silencedCount.textContent = `${silenced.size} ${silenced.size === 1 ? "neuron" : "neurons"} silenced.`;
  const toggle = document.getElementById("silence-toggle");
  if (toggle && selected !== null) toggle.textContent = silenced.has(selected) ? "Restore this neuron" : "Silence this neuron";
}

function restartAverage(): void {
  counts.fill(0);
  countFromStep = step;
  countsIncomplete = false;
}

function averagedMs(): number {
  return (step - countFromStep) * (subject?.dtMs ?? 0.1);
}

function averageRates(): Float64Array {
  const seconds = averagedMs() / 1000;
  return seconds > 0 ? counts.map((c) => c / seconds) : new Float64Array(counts.length);
}

function renderCompare(): void {
  if (!subject) return;
  const current = subject;
  const ms = averagedMs();
  ui.window.textContent = ms > 0 ? `Averaging ${ms.toFixed(0)} ms${countsIncomplete ? " (some spikes skipped at this speed)" : ""}` : "";
  ui.unpin.hidden = pinned === null;
  ui.compare.hidden = pinned === null;
  if (!pinned) return;
  const before = pinned.rates;

  ui.pinnedName.textContent = pinned.name;
  const now = averageRates();
  const change = (neuron: number) => Math.abs(now[neuron] - before[neuron]);
  const movers: number[] = [];
  for (let neuron = 0; neuron < now.length; neuron++) {
    if (current.labels.has(neuron) || change(neuron) === 0) continue;
    movers.push(neuron);
  }
  movers.sort((a, b) => change(b) - change(a));

  const rows = [...current.labels.keys(), ...movers.slice(0, 4)].map((neuron) => {
    const row = document.createElement("tr");
    const name = document.createElement("td");
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = current.nameOf(neuron);
    button.addEventListener("click", () => select(neuron));
    name.append(button);
    const delta = now[neuron] - before[neuron];
    const cells = [before[neuron].toFixed(0), now[neuron].toFixed(0), `${delta >= 0 ? "+" : "−"}${Math.abs(delta).toFixed(0)}`].map((text) => {
      const cell = document.createElement("td");
      cell.textContent = text;
      return cell;
    });
    if (Math.abs(delta) >= 1) cells[2].className = delta > 0 ? "up" : "down";
    row.append(name, ...cells);
    return row;
  });
  ui.compare.tBodies[0].replaceChildren(...rows);
}

function neuronButton(neuron: number, amount: string): HTMLLIElement {
  const item = document.createElement("li");
  const button = document.createElement("button");
  button.type = "button";
  const dot = document.createElement("i");
  dot.className = "dot";
  dot.style.background = neuronColour(palette, subject!, neuron);
  const value = document.createElement("span");
  value.className = "amount";
  value.textContent = amount;
  button.append(dot, subject!.nameOf(neuron), value);
  button.addEventListener("click", () => select(neuron));
  item.append(button);
  return item;
}

function renderTop(): void {
  if (!subject) return;
  const top = [...rates].sort((a, b) => b[1] - a[1]).slice(0, 6);
  ui.top.replaceChildren(...top.map(([neuron, hz]) => neuronButton(neuron, `${hz.toFixed(0)} Hz`)));

  const groups = subject.groups;
  if (!groups) return;
  const firing = new Map<number, number>();
  for (const neuron of rates.keys()) firing.set(groups.of(neuron), (firing.get(groups.of(neuron)) ?? 0) + 1);
  const busiest = [...firing].sort((a, b) => b[1] - a[1]).slice(0, 6);
  ui.regions.replaceChildren(
    ...busiest.map(([group, neurons]) => {
      const item = document.createElement("li");
      item.className = "region-row";
      const amount = document.createElement("span");
      amount.className = "amount";
      amount.textContent = `${neurons} firing`;
      item.append(groups.names[group], amount);
      return item;
    }),
  );
}

function buildOutputs(): void {
  if (!subject) return;
  const cards = [...subject.labels].map(([neuron, label]) => {
    const card = document.createElement("div");
    card.className = "output-card";
    card.dataset.neuron = String(neuron);
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = label;
    button.addEventListener("click", () => select(neuron));
    const meter = document.createElement("div");
    meter.className = "meter";
    meter.append(document.createElement("i"));
    const value = document.createElement("span");
    value.className = "value";
    card.append(button, meter, value);
    return card;
  });
  ui.outputs.replaceChildren(...cards);
  updateOutputs();
}

function updateOutputs(): void {
  for (const card of ui.outputs.children as HTMLCollectionOf<HTMLElement>) {
    const hz = rates.get(Number(card.dataset.neuron)) ?? 0;
    card.querySelector<HTMLElement>(".meter i")!.style.width = `${Math.min(100, (hz / 150) * 100)}%`;
    card.querySelector(".value")!.textContent = `${hz.toFixed(0)} Hz`;
  }
}

function neighbourLists(inputs: Edge[], outputs: Edge[]): HTMLElement[] {
  const list = (title: string, edges: Edge[]) => {
    const heading = document.createElement("h3");
    heading.textContent = title;
    const items = document.createElement("ol");
    items.className = "neuron-list";
    items.append(...edges.slice(0, 5).map((e) => neuronButton(e.other, `${e.weight > 0 ? "+" : ""}${e.weight} syn`)));
    return [heading, items];
  };
  return [...list("Strongest inputs", inputs), ...list("Strongest outputs", outputs)];
}

function flywireLink(id: string): HTMLAnchorElement {
  const link = document.createElement("a");
  link.href = `https://codex.flywire.ai/app/cell_details?root_id=${id}`;
  link.target = "_blank";
  link.rel = "noreferrer";
  link.textContent = id;
  return link;
}

function renderNeighbours(message: NeighboursMessage): void {
  document.getElementById("selected-id")?.replaceChildren(flywireLink(message.id));
  const toEdges = (pairs: [number, number][]) => pairs.map(([other, weight]) => ({ other, weight }));
  document.getElementById("neighbours")?.replaceChildren(...neighbourLists(toEdges(message.inputs), toEdges(message.outputs)));
}

function select(neuron: number | null): void {
  selected = neuron;
  stage.setSelected(neuron);
  rasterView.setSelected(neuron);
  traceView.clear();
  connection.send({ type: "watch", neuron });
  ui.trace.hidden = false;

  if (!subject || neuron === null) {
    const hint = document.createElement("p");
    hint.className = "note";
    hint.textContent = "Click a neuron, or pick one of the most active.";
    ui.inspector.replaceChildren(hint);
    return;
  }

  const facts = document.createElement("dl");
  facts.className = "facts";
  const fact = (name: string, value: string | Node, id?: string) => {
    const dt = document.createElement("dt");
    dt.textContent = name;
    const dd = document.createElement("dd");
    if (id) dd.id = id;
    dd.append(value);
    facts.append(dt, dd);
  };

  const known = subject.neighbours(neuron);
  fact("Name", subject.nameOf(neuron));
  fact("FlyWire ID", known ? flywireLink(known.id) : "…", "selected-id");
  for (const [name, value] of subject.facts(neuron)) fact(name, value);
  fact("Rate", "0 Hz", "selected-rate");

  const neighbours = document.createElement("div");
  neighbours.id = "neighbours";
  if (known) neighbours.append(...neighbourLists(known.inputs, known.outputs));
  else connection.send({ type: "neighbours", neuron });

  const toggle = document.createElement("button");
  toggle.id = "silence-toggle";
  toggle.type = "button";
  toggle.className = "silence";
  toggle.title = "A silenced neuron still fires, but its outputs are cut";
  toggle.addEventListener("click", () => setSilenced([neuron], !silenced.has(neuron)));

  ui.inspector.replaceChildren(facts, toggle, ui.trace, neighbours);
  renderSilenced();
  renderSelectedRate();
}

function renderSelectedRate(): void {
  const cell = document.getElementById("selected-rate");
  if (cell && selected !== null) cell.textContent = `${(rates.get(selected) ?? 0).toFixed(0)} Hz`;
}

ui.circuit.addEventListener("change", () => load(ui.circuit.value));

ui.stimulus.addEventListener("change", () => {
  // Switch off the old stimulus before the button starts meaning the new one.
  if (stimulating) {
    for (const s of subject?.stimuli ?? []) if (s.sensory) connection.send({ type: "stimulate", rateHz: 0, neurons: s.sensory });
    stimulating = false;
    restartAverage();
  }
  renderStimulus();
});

ui.stimulate.addEventListener("click", () => {
  stimulating = !stimulating;
  restartAverage();
  sendStimulus(stimulating ? Number(ui.rate.value) : 0);
  if (stimulating && !running) connection.send({ type: "play" });
  renderStimulus();
});

ui.rate.addEventListener("input", () => {
  ui.rateOut.textContent = `${ui.rate.value} Hz`;
  if (!stimulating) return;
  restartAverage();
  sendStimulus(Number(ui.rate.value));
});

ui.restoreAll.addEventListener("click", () => setSilenced([...silenced], false));

ui.pin.addEventListener("click", () => {
  pinned = { name: stimulating ? `${ui.rate.value} Hz` : "No stimulus", rates: averageRates() };
  renderCompare();
});

ui.unpin.addEventListener("click", () => {
  pinned = null;
  renderCompare();
});

ui.tap.addEventListener("click", () => {
  world.tapAntenna();
  if (!running) connection.send({ type: "play" });
});
ui.clearDrops.addEventListener("click", () => world.clearDrops());
for (const radio of document.querySelectorAll<HTMLInputElement>("input[name=drop]")) {
  radio.addEventListener("change", () => (world.tool = radio.value as DropKind));
}
ui.flyCanvas.addEventListener("click", () => {
  sendSenses();
  if (!running) connection.send({ type: "play" });
});

ui.play.addEventListener("click", () => connection.send({ type: running ? "pause" : "play" }));
ui.step.addEventListener("click", () => connection.send({ type: "step", steps: Math.round(1 / (subject?.dtMs ?? 0.1)) }));
ui.reset.addEventListener("click", () => connection.send({ type: "reset" }));
ui.speed.addEventListener("change", () => connection.send({ type: "speed", value: speedValue() }));

document.addEventListener("keydown", (event) => {
  const target = event.target as HTMLElement;
  if (event.code !== "Space" || ["BUTTON", "SELECT", "INPUT", "A"].includes(target.tagName) || ui.play.disabled) return;
  event.preventDefault();
  ui.play.click();
});

function applyTheme(): void {
  palette = readPalette();
  circuitView.setPalette(palette);
  brainView.setPalette(palette);
  world.setPalette(palette);
  rasterView.setPalette(palette);
  traceView.setPalette(palette);
  $("theme").textContent = isDark() ? "Light" : "Dark";
}
darkQuery.addEventListener("change", applyTheme);
$("theme").addEventListener("click", () => {
  const next = isDark() ? "light" : "dark";
  document.documentElement.dataset.theme = next;
  try {
    localStorage.setItem("theme", next);
  } catch {
    // Private windows may refuse storage; the choice then lasts for this visit.
  }
  applyTheme();
});
applyTheme();

function animate(now: number): void {
  stage.draw(now);
  if (embodied) world.draw();
  rasterView.draw(step);
  if (selected !== null) traceView.draw();
  requestAnimationFrame(animate);
}
requestAnimationFrame(animate);
