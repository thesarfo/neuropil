import { fitCanvas, neuronColour, type Palette } from "./canvas";
import type { BrainModel } from "./brainModel";

const FLASH_HALF_LIFE_MS = 220;
const FULL_RATE_HZ = 150;

/**
 * Every neuron at its real position, seen from the front. Silent neurons are a faint backdrop
 * drawn once; the few hundred that are firing are drawn on top each frame, brighter the faster
 * they fire, with a flash on each spike.
 */
export class BrainView {
  private readonly context: CanvasRenderingContext2D;
  private model: BrainModel | null = null;
  private width = 0;
  private height = 0;
  private px = new Float32Array(0);
  private py = new Float32Array(0);
  private backdrop: HTMLCanvasElement | null = null;
  private readonly flash = new Map<number, number>();
  private rates = new Map<number, number>();
  private selected: number | null = null;
  private lastDraw = performance.now();

  constructor(
    private readonly canvas: HTMLCanvasElement,
    private palette: Palette,
    onSelect: (neuron: number | null) => void,
  ) {
    this.context = canvas.getContext("2d")!;
    fitCanvas(canvas, (width, height) => {
      this.width = width;
      this.height = height;
      this.layout();
    });
    canvas.addEventListener("click", (event) => onSelect(this.neuronAt(event.offsetX, event.offsetY)));
  }

  setModel(model: BrainModel): void {
    this.model = model;
    this.flash.clear();
    this.rates = new Map();
    this.selected = null;
    this.layout();
  }

  setPalette(palette: Palette): void {
    this.palette = palette;
    this.backdrop = null;
  }

  setSelected(neuron: number | null): void {
    this.selected = neuron;
  }

  setRates(rates: Map<number, number>): void {
    this.rates = rates;
  }

  spike(neuron: number): void {
    this.flash.set(neuron, 1);
  }

  clear(): void {
    this.flash.clear();
    this.rates = new Map();
  }

  private layout(): void {
    const model = this.model;
    if (!model || this.width === 0) return;
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (let i = 0; i < model.count; i++) {
      minX = Math.min(minX, model.x[i]);
      maxX = Math.max(maxX, model.x[i]);
      minY = Math.min(minY, model.y[i]);
      maxY = Math.max(maxY, model.y[i]);
    }
    const pad = 16;
    const scale = Math.min((this.width - 2 * pad) / (maxX - minX), (this.height - 2 * pad) / (maxY - minY));
    const offsetX = (this.width - (maxX - minX) * scale) / 2;
    const offsetY = (this.height - (maxY - minY) * scale) / 2;
    this.px = new Float32Array(model.count);
    this.py = new Float32Array(model.count);
    for (let i = 0; i < model.count; i++) {
      this.px[i] = offsetX + (model.x[i] - minX) * scale;
      this.py[i] = offsetY + (model.y[i] - minY) * scale;
    }
    this.backdrop = null;
  }

  /** Prefers a firing neuron near the click, since those are the ones worth inspecting. */
  private neuronAt(x: number, y: number): number | null {
    const nearest = (candidates: Iterable<number>, reach: number) => {
      let best: number | null = null;
      let bestDistance = reach * reach;
      for (const i of candidates) {
        const d = (this.px[i] - x) ** 2 + (this.py[i] - y) ** 2;
        if (d < bestDistance) {
          bestDistance = d;
          best = i;
        }
      }
      return best;
    };
    return nearest(this.rates.keys(), 12) ?? nearest(this.model?.labels.keys() ?? [], 12) ?? nearest(this.px.keys(), 5);
  }

  private drawBackdrop(model: BrainModel): HTMLCanvasElement {
    const layer = document.createElement("canvas");
    layer.width = this.canvas.width;
    layer.height = this.canvas.height;
    const c = layer.getContext("2d")!;
    const ratio = this.canvas.width / this.width;
    c.setTransform(ratio, 0, 0, ratio, 0, 0);
    c.fillStyle = this.palette.muted;
    c.globalAlpha = 0.22;
    for (let i = 0; i < model.count; i++) c.fillRect(this.px[i], this.py[i], 1, 1);
    return layer;
  }

  draw(now: number): void {
    const c = this.context;
    const model = this.model;
    c.clearRect(0, 0, this.width, this.height);
    if (!model || this.width === 0) return;

    this.backdrop ??= this.drawBackdrop(model);
    c.drawImage(this.backdrop, 0, 0, this.width, this.height);

    for (const [neuron, hz] of this.rates) {
      c.globalAlpha = 0.3 + 0.7 * Math.min(1, hz / FULL_RATE_HZ);
      c.fillStyle = neuronColour(this.palette, model, neuron);
      c.beginPath();
      c.arc(this.px[neuron], this.py[neuron], 1.8, 0, Math.PI * 2);
      c.fill();
    }

    const decay = Math.pow(0.5, (now - this.lastDraw) / FLASH_HALF_LIFE_MS);
    this.lastDraw = now;
    for (const [neuron, glow] of this.flash) {
      c.globalAlpha = glow * 0.5;
      c.fillStyle = neuronColour(this.palette, model, neuron);
      c.beginPath();
      c.arc(this.px[neuron], this.py[neuron], 2 + 5 * glow, 0, Math.PI * 2);
      c.fill();
      if (glow * decay < 0.03) this.flash.delete(neuron);
      else this.flash.set(neuron, glow * decay);
    }
    c.globalAlpha = 1;

    c.font = "600 12px system-ui, -apple-system, 'Segoe UI', sans-serif";
    c.textAlign = "left";
    for (const [neuron, label] of model.labels) {
      c.strokeStyle = this.palette.output;
      c.lineWidth = 1.5;
      c.beginPath();
      c.arc(this.px[neuron], this.py[neuron], 5, 0, Math.PI * 2);
      c.stroke();
      c.fillStyle = this.palette.fg;
      c.fillText(label, this.px[neuron] + 9, this.py[neuron] + 4);
    }

    if (this.selected !== null) {
      c.strokeStyle = this.palette.select;
      c.lineWidth = 2;
      c.beginPath();
      c.arc(this.px[this.selected], this.py[this.selected], 8, 0, Math.PI * 2);
      c.stroke();
    }
  }
}
