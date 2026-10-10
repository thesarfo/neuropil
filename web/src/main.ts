import { neuronColour, readPalette } from "./canvas";
import { CircuitView } from "./circuitView";
import { CircuitModel } from "./model";
import type { FrameMessage, ServerMessage } from "./protocol";
import { RasterView } from "./rasterView";
import { Connection, type ConnectionState } from "./socket";
import { TraceView } from "./traceView";

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const ui = {
  status: $("status"),
  circuit: $<HTMLSelectElement>("circuit"),
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
  inspector: $("inspector-body"),
  trace: $<HTMLCanvasElement>("trace-view"),
  top: $("top"),
};

let palette = readPalette();
let model: CircuitModel | null = null;
let running = false;
let stimulating = false;
let step = 0;
let selected: number | null = null;
let rates = new Map<number, number>();
let lastListUpdate = 0;

// Spike counts since the stimulus last changed, for average rates steadier than the 100 ms window.
let counts = new Float64Array(0);
let countFromStep = 0;
let countsIncomplete = false;
let pinned: { name: string; rates: Float64Array } | null = null;

const circuitView = new CircuitView($<HTMLCanvasElement>("circuit-view"), palette, select);
const rasterView = new RasterView($<HTMLCanvasElement>("raster-view"), palette);
const traceView = new TraceView(ui.trace, palette);

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
    case "hello":
      ui.circuit.replaceChildren(...message.circuits.map((c) => new Option(`${c.title} (${c.neurons} neurons)`, c.name)));
      if (message.circuits.length === 0) {
        showEmpty("No circuits found.\nBuild one with `neuropil circuit` and put it in data/circuits/.");
        ui.status.textContent = "Connected";
        return;
      }
      connection.send({ type: "load", circuit: model?.name ?? message.circuits[0].name });
      break;

    case "circuit":
      model = new CircuitModel(message);
      ui.circuit.value = model.name;
      ui.empty.hidden = true;
      stimulating = false;
      rates = new Map();
      step = 0;
      counts = new Float64Array(model.count);
      pinned = null;
      restartAverage();
      ui.about.textContent = model.info.about;
      renderCompare();
      circuitView.setModel(model);
      rasterView.setModel(model);
      traceView.clear(model.dtMs);
      select(null);
      buildOutputs();
      setEnabled(true);
      renderStimulate();
      connection.send({ type: "speed", value: speedValue() });
      ui.status.textContent = `${model.count} neurons, ${model.edges.length.toLocaleString()} connections`;
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

function onFrame(frame: FrameMessage): void {
  if (!model) return;
  if (frame.step < step) {
    circuitView.clear();
    rasterView.clear();
    traceView.clear();
    step = frame.step;
    restartAverage();
  }
  step = frame.step;
  if (frame.truncated) countsIncomplete = true;
  running = frame.running;
  for (const [at, neuron] of frame.spikes) {
    circuitView.spike(neuron);
    counts[neuron]++;
    rasterView.add(at, neuron);
  }
  rates = new Map(frame.rates);
  if (frame.trace && frame.trace.neuron === selected) traceView.add(frame.trace.fromStep, frame.trace.mv);

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
  for (const control of [ui.circuit, ui.stimulate, ui.rate, ui.play, ui.step, ui.reset, ui.speed, ui.pin]) control.disabled = !enabled;
}

function showEmpty(text: string): void {
  ui.empty.textContent = text;
  ui.empty.hidden = false;
}

function speedValue(): number | "max" {
  return ui.speed.value === "max" ? "max" : Number(ui.speed.value);
}

function renderStimulate(): void {
  ui.stimulate.setAttribute("aria-pressed", String(stimulating));
  ui.stimulate.textContent = stimulating ? (model?.info.stop ?? "Stop") : (model?.info.start ?? "Stimulate");
}

function restartAverage(): void {
  counts.fill(0);
  countFromStep = step;
  countsIncomplete = false;
}

function averagedMs(): number {
  return (step - countFromStep) * (model?.dtMs ?? 0.1);
}

function averageRates(): Float64Array {
  const seconds = averagedMs() / 1000;
  return seconds > 0 ? counts.map((c) => c / seconds) : new Float64Array(counts.length);
}

function renderCompare(): void {
  if (!model) return;
  const ms = averagedMs();
  ui.window.textContent = ms > 0 ? `Averaging ${ms.toFixed(0)} ms${countsIncomplete ? " (some spikes skipped at this speed)" : ""}` : "";
  ui.unpin.hidden = pinned === null;
  ui.compare.hidden = pinned === null;
  if (!pinned) return;

  ui.pinnedName.textContent = pinned.name;
  const now = averageRates();
  const labelled = model.labels.flatMap((label, neuron) => (label ? [neuron] : []));
  const movers = [...now.keys()]
    .filter((neuron) => !model!.labels[neuron])
    .sort((a, b) => Math.abs(now[b] - pinned!.rates[b]) - Math.abs(now[a] - pinned!.rates[a]))
    .slice(0, 4);

  const rows = [...labelled, ...movers].map((neuron) => {
    const row = document.createElement("tr");
    const name = document.createElement("td");
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = model!.nameOf(neuron);
    button.addEventListener("click", () => select(neuron));
    name.append(button);
    const change = now[neuron] - pinned!.rates[neuron];
    const cells = [pinned!.rates[neuron].toFixed(0), now[neuron].toFixed(0), `${change >= 0 ? "+" : "−"}${Math.abs(change).toFixed(0)}`].map((text) => {
      const cell = document.createElement("td");
      cell.textContent = text;
      return cell;
    });
    if (Math.abs(change) >= 1) cells[2].className = change > 0 ? "up" : "down";
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
  dot.style.background = neuronColour(palette, model!, neuron);
  const value = document.createElement("span");
  value.className = "amount";
  value.textContent = amount;
  button.append(dot, model!.nameOf(neuron), value);
  button.addEventListener("click", () => select(neuron));
  item.append(button);
  return item;
}

function renderTop(): void {
  if (!model) return;
  const top = [...rates].sort((a, b) => b[1] - a[1]).slice(0, 6);
  ui.top.replaceChildren(...top.map(([neuron, hz]) => neuronButton(neuron, `${hz.toFixed(0)} Hz`)));
}

function buildOutputs(): void {
  if (!model) return;
  const cards = model.labels.flatMap((label, neuron) => {
    if (!label) return [];
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
    return [card];
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

function select(neuron: number | null): void {
  selected = neuron;
  circuitView.setSelected(neuron);
  rasterView.setSelected(neuron);
  traceView.clear();
  connection.send({ type: "watch", neuron });

  ui.trace.hidden = false;
  if (!model || neuron === null) {
    const hint = document.createElement("p");
    hint.className = "hint";
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
  const link = document.createElement("a");
  link.href = `https://codex.flywire.ai/app/cell_details?root_id=${model.ids[neuron]}`;
  link.target = "_blank";
  link.rel = "noreferrer";
  link.textContent = model.ids[neuron];

  fact("Name", model.nameOf(neuron));
  fact("FlyWire ID", link);
  fact("Kind", model.kind(neuron));
  fact("Layer", model.layer[neuron] === 0 ? "sensors" : model.layer[neuron] === model.unreachedLayer ? "other" : `hop ${model.layer[neuron]}`);
  fact("Rate", "0 Hz", "selected-rate");
  fact("Inputs", `${model.inputs[neuron].length}`);
  fact("Outputs", `${model.outputs[neuron].length}`);

  const list = (title: string, edges: { other: number; weight: number }[]) => {
    const heading = document.createElement("h3");
    heading.textContent = title;
    const items = document.createElement("ol");
    items.className = "neuron-list";
    items.append(...edges.slice(0, 5).map((e) => neuronButton(e.other, `${e.weight > 0 ? "+" : ""}${e.weight} syn`)));
    return [heading, items];
  };

  ui.inspector.replaceChildren(facts, ui.trace, ...list("Strongest inputs", model.inputs[neuron]), ...list("Strongest outputs", model.outputs[neuron]));
  renderSelectedRate();
}

function renderSelectedRate(): void {
  const cell = document.getElementById("selected-rate");
  if (cell && selected !== null) cell.textContent = `${(rates.get(selected) ?? 0).toFixed(0)} Hz`;
}

ui.circuit.addEventListener("change", () => connection.send({ type: "load", circuit: ui.circuit.value }));

ui.stimulate.addEventListener("click", () => {
  stimulating = !stimulating;
  restartAverage();
  connection.send({ type: "stimulate", rateHz: stimulating ? Number(ui.rate.value) : 0 });
  if (stimulating && !running) connection.send({ type: "play" });
  renderStimulate();
});

ui.rate.addEventListener("input", () => {
  ui.rateOut.textContent = `${ui.rate.value} Hz`;
  if (!stimulating) return;
  restartAverage();
  connection.send({ type: "stimulate", rateHz: Number(ui.rate.value) });
});

ui.pin.addEventListener("click", () => {
  pinned = { name: stimulating ? `${ui.rate.value} Hz` : "No stimulus", rates: averageRates() };
  renderCompare();
});

ui.unpin.addEventListener("click", () => {
  pinned = null;
  renderCompare();
});

ui.play.addEventListener("click", () => connection.send({ type: running ? "pause" : "play" }));
ui.step.addEventListener("click", () => connection.send({ type: "step", steps: Math.round(1 / (model?.dtMs ?? 0.1)) }));
ui.reset.addEventListener("click", () => connection.send({ type: "reset" }));
ui.speed.addEventListener("change", () => connection.send({ type: "speed", value: speedValue() }));

document.addEventListener("keydown", (event) => {
  const target = event.target as HTMLElement;
  if (event.code !== "Space" || ["BUTTON", "SELECT", "INPUT", "A"].includes(target.tagName) || ui.play.disabled) return;
  event.preventDefault();
  ui.play.click();
});

matchMedia("(prefers-color-scheme: light)").addEventListener("change", () => {
  palette = readPalette();
  circuitView.setPalette(palette);
  rasterView.setPalette(palette);
  traceView.setPalette(palette);
});

function animate(now: number): void {
  circuitView.draw(now);
  rasterView.draw(step);
  if (selected !== null) traceView.draw();
  requestAnimationFrame(animate);
}
requestAnimationFrame(animate);
