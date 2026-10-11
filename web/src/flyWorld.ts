import { fitCanvas, type Palette } from "./canvas";

export type DropKind = "sugar" | "water";
export type Senses = { sugar: number; water: number; antenna: number };
export type MotorRates = { proboscis?: number; groom?: number };

type Drop = { x: number; y: number; kind: DropKind; amount: number };

// Motor neuron firing rate at which an action is fully expressed.
const PROBOSCIS_FULL_HZ = 40;
const GROOM_FULL_HZ = 15;
// How quickly the body follows the brain, in simulated milliseconds.
const MUSCLE_MS = 30;

const WALK_LENGTHS_PER_S = 4;
const MEAL_MS = 700;
const TAP_MS = 400;

/**
 * A fly seen from above in a small arena. Everything is in simulated time, so it slows down
 * and pauses with the brain.
 *
 * From the brain: proboscis extension (MN9 rate) and antennal grooming (aDN1, aDN2, aBN1 rates),
 * and the fly stops walking while it does either.
 * Hand-written: walking, and steering towards a drop it is close to. The connectome ends at the
 * neck, so the leg circuits that would do this are not in it.
 */
export class FlyWorld {
  private readonly context: CanvasRenderingContext2D;
  private width = 0;
  private height = 0;
  private x = 0;
  private y = 0;
  private heading = -Math.PI / 2;
  private gait = 0;
  private wander = 0;
  private timeMs = 0;
  private tapUntilMs = -1;
  private drops: Drop[] = [];
  private placed = false;

  extension = 0;
  grooming = 0;
  tool: DropKind = "sugar";

  constructor(canvas: HTMLCanvasElement, private palette: Palette) {
    this.context = canvas.getContext("2d")!;
    fitCanvas(canvas, (width, height) => {
      this.width = width;
      this.height = height;
      if (!this.placed && width > 0) this.reset();
    });
    canvas.addEventListener("click", (event) => {
      if (Math.hypot(event.offsetX - this.headX, event.offsetY - this.headY) < this.length * 0.3) this.tapAntenna();
      else this.drops.push({ x: event.offsetX, y: event.offsetY, kind: this.tool, amount: 1 });
    });
  }

  private get length(): number {
    return Math.min(this.width, this.height) * 0.2;
  }

  private get headX(): number {
    return this.x + Math.cos(this.heading) * this.length * 0.33;
  }

  private get headY(): number {
    return this.y + Math.sin(this.heading) * this.length * 0.33;
  }

  setPalette(palette: Palette): void {
    this.palette = palette;
  }

  reset(): void {
    this.placed = this.width > 0;
    this.x = this.width / 2;
    this.y = this.height * 0.62;
    this.heading = -Math.PI / 2;
    this.extension = this.grooming = 0;
    this.timeMs = 0;
    this.tapUntilMs = -1;
    this.drops = [];
  }

  clearDrops(): void {
    this.drops = [];
  }

  tapAntenna(): void {
    this.tapUntilMs = this.timeMs + TAP_MS;
  }

  /** What the fly feels: 1 when a drop is at its mouthparts, 0.5 when only its front feet reach it. */
  sense(): Senses {
    const senses: Senses = { sugar: 0, water: 0, antenna: this.timeMs < this.tapUntilMs ? 1 : 0 };
    for (const drop of this.drops) {
      const distance = Math.hypot(drop.x - this.headX, drop.y - this.headY) - this.dropRadius(drop);
      const level = distance < this.length * 0.28 ? 1 : distance < this.length * 0.6 ? 0.5 : 0;
      senses[drop.kind] = Math.max(senses[drop.kind], level);
    }
    return senses;
  }

  private dropRadius(drop: Drop): number {
    return this.length * 0.16 * Math.sqrt(drop.amount);
  }

  advance(dtMs: number, motor: MotorRates): void {
    if (dtMs <= 0 || this.width === 0) return;
    this.timeMs += dtMs;
    const follow = 1 - Math.exp(-dtMs / MUSCLE_MS);
    this.extension += (Math.min(1, (motor.proboscis ?? 0) / PROBOSCIS_FULL_HZ) - this.extension) * follow;
    this.grooming += (Math.min(1, (motor.groom ?? 0) / GROOM_FULL_HZ) - this.grooming) * follow;

    // Eat what the extended proboscis is touching.
    for (const drop of this.drops) {
      const reach = Math.hypot(drop.x - this.headX, drop.y - this.headY) - this.dropRadius(drop);
      if (this.extension > 0.5 && reach < this.length * 0.28) drop.amount -= dtMs / MEAL_MS;
    }
    this.drops = this.drops.filter((drop) => drop.amount > 0.02);

    // Hand-written walking: wander, turn towards a nearby drop, keep away from the walls.
    const busy = Math.max(this.extension, this.grooming);
    const speed = (1 - busy) ** 2;
    const seconds = dtMs / 1000;
    this.wander += (Math.sin(this.timeMs / 230) + Math.sin(this.timeMs / 610 + 1.3)) * 0.9 * seconds;
    let turn = this.wander * seconds * 2;

    const nearest = this.drops
      .map((drop) => ({ drop, distance: Math.hypot(drop.x - this.headX, drop.y - this.headY) }))
      .filter((d) => d.distance < this.length * 3.5)
      .sort((a, b) => a.distance - b.distance)[0];
    const margin = this.length * 0.8;
    const nearWall = this.x < margin || this.x > this.width - margin || this.y < margin || this.y > this.height - margin;
    const goal = nearest ? nearest.drop : nearWall ? { x: this.width / 2, y: this.height / 2 } : null;
    if (goal) {
      const wanted = Math.atan2(goal.y - this.headY, goal.x - this.headX);
      const error = Math.atan2(Math.sin(wanted - this.heading), Math.cos(wanted - this.heading));
      turn = Math.max(-1, Math.min(1, error * 3)) * 4 * seconds;
    }

    this.heading += turn * speed;
    const stride = WALK_LENGTHS_PER_S * this.length * seconds * speed;
    this.x = Math.max(margin / 2, Math.min(this.width - margin / 2, this.x + Math.cos(this.heading) * stride));
    this.y = Math.max(margin / 2, Math.min(this.height - margin / 2, this.y + Math.sin(this.heading) * stride));
    this.gait += (stride / this.length) * 9;
  }

  draw(): void {
    const c = this.context;
    c.clearRect(0, 0, this.width, this.height);
    if (this.width === 0) return;
    const p = this.palette;
    const L = this.length;

    for (const drop of this.drops) {
      c.globalAlpha = 0.85;
      c.fillStyle = drop.kind === "sugar" ? p.sugar : p.water;
      c.beginPath();
      c.arc(drop.x, drop.y, this.dropRadius(drop), 0, Math.PI * 2);
      c.fill();
    }
    c.globalAlpha = 1;

    c.save();
    c.translate(this.x, this.y);
    c.rotate(this.heading);
    c.lineCap = "round";

    // Legs: two tripods swinging in turn. While grooming, the front pair sweeps over the head.
    c.strokeStyle = p.fg;
    c.lineWidth = Math.max(1.2, L * 0.022);
    const swing = Math.sin(this.gait);
    [0.14, 0.02, -0.1].forEach((attach, pair) => {
      for (const side of [-1, 1]) {
        const tripod = (pair + (side > 0 ? 1 : 0)) % 2 === 0 ? swing : -swing;
        let footX = attach + [0.26, 0.02, -0.26][pair] + tripod * 0.07;
        let footY = side * [0.3, 0.36, 0.32][pair];
        let kneeX = attach + [0.12, 0.0, -0.1][pair];
        let kneeY = side * 0.2;
        if (pair === 0 && this.grooming > 0.02) {
          const rub = Math.sin(this.timeMs / 18) * 0.05;
          const g = this.grooming;
          footX += (0.44 + rub - footX) * g;
          footY += (side * 0.05 - footY) * g;
          kneeX += (0.3 - kneeX) * g;
          kneeY += (side * 0.17 - kneeY) * g;
        }
        c.beginPath();
        c.moveTo(attach * L, side * 0.09 * L);
        c.lineTo(kneeX * L, kneeY * L);
        c.lineTo(footX * L, footY * L);
        c.stroke();
      }
    });

    // Wings, then abdomen, thorax and head.
    c.fillStyle = p.muted;
    c.globalAlpha = 0.28;
    for (const side of [-1, 1]) {
      c.beginPath();
      c.ellipse(-0.3 * L, side * 0.1 * L, 0.34 * L, 0.11 * L, side * 0.16, 0, Math.PI * 2);
      c.fill();
    }
    c.globalAlpha = 1;
    c.fillStyle = p.fg;
    c.beginPath();
    c.ellipse(-0.27 * L, 0, 0.27 * L, 0.14 * L, 0, 0, Math.PI * 2);
    c.fill();
    c.beginPath();
    c.ellipse(0.06 * L, 0, 0.15 * L, 0.14 * L, 0, 0, Math.PI * 2);
    c.fill();
    c.beginPath();
    c.arc(0.27 * L, 0, 0.11 * L, 0, Math.PI * 2);
    c.fill();

    c.fillStyle = p.output;
    for (const side of [-1, 1]) {
      c.beginPath();
      c.ellipse(0.28 * L, side * 0.085 * L, 0.055 * L, 0.04 * L, side * 0.5, 0, Math.PI * 2);
      c.fill();
    }

    // Antennae, lit while they are being touched.
    const touched = this.timeMs < this.tapUntilMs;
    c.strokeStyle = touched ? p.sensory : p.fg;
    c.lineWidth = Math.max(1.5, L * (touched ? 0.04 : 0.025));
    for (const side of [-1, 1]) {
      c.beginPath();
      c.moveTo(0.36 * L, side * 0.03 * L);
      c.lineTo(0.45 * L, side * 0.07 * L);
      c.stroke();
    }

    // Proboscis, as long as the brain says.
    if (this.extension > 0.02) {
      const tip = (0.37 + 0.2 * this.extension) * L;
      c.strokeStyle = p.output;
      c.lineWidth = Math.max(2, L * 0.04);
      c.beginPath();
      c.moveTo(0.36 * L, 0);
      c.lineTo(tip, 0);
      c.stroke();
      c.fillStyle = p.output;
      c.beginPath();
      c.arc(tip, 0, L * 0.035, 0, Math.PI * 2);
      c.fill();
    }
    c.restore();
  }
}
