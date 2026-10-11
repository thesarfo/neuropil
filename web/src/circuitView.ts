import { CANVAS_SANS, fitCanvas, neuronColour, type Palette } from "./canvas";
import type { CircuitModel } from "./model";

const FLASH_HALF_LIFE_MS = 180;
const STRONG_EDGES = 1400;
const MONO_FONT = `11px ${CANVAS_SANS}`;
const LABEL_FONT = `600 12px ${CANVAS_SANS}`;

/** Neurons in columns from sensors to outputs. A neuron lights up when it spikes and fades. */
export class CircuitView {
  private readonly context: CanvasRenderingContext2D;
  private model: CircuitModel | null = null;
  private width = 0;
  private height = 0;
  private x = new Float32Array(0);
  private y = new Float32Array(0);
  private radius = 4;
  private flash = new Float32Array(0);
  private edgeLayer: HTMLCanvasElement | null = null;
  private selected: number | null = null;
  private lastDraw = performance.now();

  constructor(
    private readonly canvas: HTMLCanvasElement,
    private palette: Palette,
    private readonly onSelect: (neuron: number | null) => void,
  ) {
    this.context = canvas.getContext("2d")!;
    fitCanvas(canvas, (width, height) => {
      this.width = width;
      this.height = height;
      this.layout();
    });
    canvas.addEventListener("click", (event) => this.onSelect(this.neuronAt(event.offsetX, event.offsetY)));
  }

  setModel(model: CircuitModel): void {
    this.model = model;
    this.flash = new Float32Array(model.count);
    this.selected = null;
    this.layout();
  }

  setPalette(palette: Palette): void {
    this.palette = palette;
    this.edgeLayer = null;
  }

  setSelected(neuron: number | null): void {
    this.selected = neuron;
  }

  spike(neuron: number): void {
    this.flash[neuron] = 1;
  }

  clear(): void {
    this.flash.fill(0);
  }

  /** Picks the largest spacing at which every layer's grid fits the canvas. */
  private layout(): void {
    const model = this.model;
    if (!model || this.width === 0) return;
    const pad = 28;
    const gapColumns = 1.6;
    const w = this.width - 2 * pad;
    const h = this.height - 2 * pad - 14;
    const largest = Math.max(...model.layers.map((l) => l.length));

    let best = { spacing: 0, rows: 1 };
    for (let rows = 1; rows <= largest; rows++) {
      const columns = model.layers.reduce((sum, l) => sum + Math.ceil(l.length / rows), 0) + gapColumns * (model.layers.length - 1);
      const spacing = Math.min(h / rows, w / columns);
      if (spacing > best.spacing) best = { spacing, rows };
    }

    const { spacing, rows } = best;
    this.radius = Math.max(1.5, Math.min(7, spacing * 0.32));
    this.x = new Float32Array(model.count);
    this.y = new Float32Array(model.count);
    const totalColumns = model.layers.reduce((sum, l) => sum + Math.ceil(l.length / rows), 0) + gapColumns * (model.layers.length - 1);
    let column = (w / spacing - totalColumns) / 2;
    for (const layer of model.layers) {
      const layerColumns = Math.ceil(layer.length / rows);
      layer.forEach((neuron, k) => {
        const inColumn = Math.min(rows, layer.length - Math.floor(k / rows) * rows);
        this.x[neuron] = pad + (column + Math.floor(k / rows) + 0.5) * spacing;
        this.y[neuron] = pad + 14 + h / 2 + ((k % rows) - (inColumn - 1) / 2) * spacing;
      });
      column += layerColumns + gapColumns;
    }
    this.edgeLayer = null;
  }

  private neuronAt(px: number, py: number): number | null {
    const model = this.model;
    if (!model) return null;
    let best: number | null = null;
    let bestDistance = Math.max(10, this.radius * 2) ** 2;
    for (let i = 0; i < model.count; i++) {
      const d = (this.x[i] - px) ** 2 + (this.y[i] - py) ** 2;
      if (d < bestDistance) {
        bestDistance = d;
        best = i;
      }
    }
    return best;
  }

  /** The strongest connections, drawn once and reused every frame. */
  private drawEdgeLayer(model: CircuitModel): HTMLCanvasElement {
    const layer = document.createElement("canvas");
    layer.width = this.canvas.width;
    layer.height = this.canvas.height;
    const c = layer.getContext("2d")!;
    const ratio = this.canvas.width / this.width;
    c.setTransform(ratio, 0, 0, ratio, 0, 0);
    c.lineWidth = 0.6;

    const strongest = [...model.edges].sort((a, b) => Math.abs(b[2]) - Math.abs(a[2])).slice(0, STRONG_EDGES);
    const top = Math.abs(strongest[0]?.[2] ?? 1);
    for (const [pre, post, weight] of strongest) {
      c.globalAlpha = 0.06 + 0.22 * Math.sqrt(Math.abs(weight) / top);
      c.strokeStyle = weight < 0 ? this.palette.inhibitory : this.palette.excitatory;
      c.beginPath();
      c.moveTo(this.x[pre], this.y[pre]);
      c.lineTo(this.x[post], this.y[post]);
      c.stroke();
    }
    return layer;
  }

  draw(now: number): void {
    const c = this.context;
    const model = this.model;
    c.clearRect(0, 0, this.width, this.height);
    if (!model || this.width === 0) return;

    this.edgeLayer ??= this.drawEdgeLayer(model);
    c.drawImage(this.edgeLayer, 0, 0, this.width, this.height);

    const decay = Math.pow(0.5, (now - this.lastDraw) / FLASH_HALF_LIFE_MS);
    this.lastDraw = now;

    c.font = MONO_FONT;
    c.textAlign = "center";
    c.fillStyle = this.palette.muted;
    model.layers.forEach((layer, index) => {
      if (layer.length === 0) return;
      const xs = layer.map((n) => this.x[n]);
      c.fillText(model.layerName(index), (Math.min(...xs) + Math.max(...xs)) / 2, 16);
    });

    if (this.selected !== null) this.drawSelectedEdges(model, this.selected);

    for (let i = 0; i < model.count; i++) {
      const glow = this.flash[i];
      this.flash[i] = glow * decay;
      const colour = neuronColour(this.palette, model, i);
      const r = this.radius * (model.labels.has(i) ? 1.5 : 1);

      if (glow > 0.02) {
        c.globalAlpha = glow * 0.35;
        c.fillStyle = colour;
        c.beginPath();
        c.arc(this.x[i], this.y[i], r + 5 * glow + 2, 0, Math.PI * 2);
        c.fill();
      }
      c.globalAlpha = 0.34 + 0.66 * glow;
      c.fillStyle = colour;
      c.beginPath();
      c.arc(this.x[i], this.y[i], r, 0, Math.PI * 2);
      c.fill();
    }
    c.globalAlpha = 1;

    c.textAlign = "left";
    c.font = LABEL_FONT;
    model.labels.forEach((label, i) => {
      c.fillStyle = this.palette.fg;
      c.fillText(label, this.x[i] + this.radius * 1.5 + 5, this.y[i] + 4);
    });

    if (this.selected !== null) {
      c.strokeStyle = this.palette.select;
      c.lineWidth = 2;
      c.beginPath();
      c.arc(this.x[this.selected], this.y[this.selected], this.radius + 4, 0, Math.PI * 2);
      c.stroke();
    }
  }

  private drawSelectedEdges(model: CircuitModel, neuron: number): void {
    const c = this.context;
    c.lineWidth = 1.2;
    const lines = [
      ...model.inputs[neuron].slice(0, 25).map((e) => ({ from: e.other, to: neuron, weight: e.weight })),
      ...model.outputs[neuron].slice(0, 25).map((e) => ({ from: neuron, to: e.other, weight: e.weight })),
    ];
    for (const { from, to, weight } of lines) {
      c.globalAlpha = 0.55;
      c.strokeStyle = weight < 0 ? this.palette.inhibitory : this.palette.excitatory;
      c.beginPath();
      c.moveTo(this.x[from], this.y[from]);
      c.lineTo(this.x[to], this.y[to]);
      c.stroke();
    }
    c.globalAlpha = 1;
  }
}
