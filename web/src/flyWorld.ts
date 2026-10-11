export type DropKind = "sugar" | "water";
export type Senses = { sugar: number; water: number; antenna: number };
export type MotorRates = { proboscis?: number; groom?: number };
export type Drop = { x: number; y: number; kind: DropKind; amount: number; waitedMs?: number; ignored?: boolean };

// Motor neuron firing rate at which an action is fully expressed.
const PROBOSCIS_FULL_HZ = 40;
const GROOM_FULL_HZ = 15;
// How quickly the body follows the brain, in simulated milliseconds.
const MUSCLE_MS = 30;

const WALK_LENGTHS_PER_S = 4;
const MEAL_MS = 700;
const TAP_MS = 400;
const TURN_RADIANS_PER_S = 8;
// How long it stands over food the brain isn't feeding on before moving on.
const PATIENCE_MS = 900;
// The body moves in steps this long, however much time a screen frame covers.
const STEP_MS = 4;
const MAX_CATCH_UP_MS = 400;
// How close the head must be to a drop's edge for the mouthparts to be on it.
const MOUTH_REACH = 0.28;

/** The arena, measured in fly body lengths. */
export const ARENA = { width: 12, height: 9 };
/** How far in front of the body's centre the head is. */
export const HEAD_FORWARD = 0.33;

/**
 * A fly in a small arena: where it is, what it feels and what it is doing. Distances are in
 * body lengths and time is simulated time, so it slows down and pauses with the brain.
 *
 * From the brain: proboscis extension (MN9 rate) and antennal grooming (aDN1, aDN2, aBN1 rates),
 * and the fly stops walking while it does either.
 * Hand-written: walking, going to a drop it is close to, and standing still once it is on it. The connectome ends at the
 * neck, so the leg circuits that would do this are not in it.
 */
export class FlyWorld {
  x = 0;
  y = 0;
  heading = 0;
  /** Advances with distance walked; the legs take their rhythm from it. */
  gait = 0;
  timeMs = 0;
  extension = 0;
  grooming = 0;
  drops: Drop[] = [];
  tool: DropKind = "sugar";

  private wander = 0;
  private tapUntilMs = -1;

  constructor() {
    this.reset();
  }

  get headX(): number {
    return this.x + Math.cos(this.heading) * HEAD_FORWARD;
  }

  get headY(): number {
    return this.y + Math.sin(this.heading) * HEAD_FORWARD;
  }

  get touched(): boolean {
    return this.timeMs < this.tapUntilMs;
  }

  reset(): void {
    this.x = ARENA.width / 2;
    this.y = ARENA.height * 0.62;
    this.heading = -Math.PI / 2;
    this.extension = this.grooming = 0;
    this.gait = this.wander = this.timeMs = 0;
    this.tapUntilMs = -1;
    this.drops = [];
  }

  clearDrops(): void {
    this.drops = [];
  }

  tapAntenna(): void {
    this.tapUntilMs = this.timeMs + TAP_MS;
  }

  /** A click on the ground: on the fly's head it taps an antenna, anywhere else it leaves a drop. */
  click(x: number, y: number): void {
    if (Math.hypot(x - this.headX, y - this.headY) < 0.3) this.tapAntenna();
    else if (x >= 0 && x <= ARENA.width && y >= 0 && y <= ARENA.height) this.drops.push({ x, y, kind: this.tool, amount: 1 });
  }

  dropRadius(drop: Drop): number {
    return 0.16 * Math.sqrt(drop.amount);
  }

  /** What the fly feels: 1 when a drop is at its mouthparts, 0.5 when only its front feet reach it. */
  sense(): Senses {
    const senses: Senses = { sugar: 0, water: 0, antenna: this.touched ? 1 : 0 };
    for (const drop of this.drops) {
      const distance = Math.hypot(drop.x - this.headX, drop.y - this.headY) - this.dropRadius(drop);
      const level = distance < MOUTH_REACH ? 1 : distance < 0.6 ? 0.5 : 0;
      senses[drop.kind] = Math.max(senses[drop.kind], level);
    }
    return senses;
  }

  /** Moves the body forward in small fixed steps, so it behaves the same at any playback speed. */
  advance(dtMs: number, motor: MotorRates): void {
    for (let left = Math.min(dtMs, MAX_CATCH_UP_MS); left > 0; left -= STEP_MS) this.step(Math.min(left, STEP_MS), motor);
  }

  private step(dtMs: number, motor: MotorRates): void {
    this.timeMs += dtMs;
    const follow = 1 - Math.exp(-dtMs / MUSCLE_MS);
    this.extension += (Math.min(1, (motor.proboscis ?? 0) / PROBOSCIS_FULL_HZ) - this.extension) * follow;
    this.grooming += (Math.min(1, (motor.groom ?? 0) / GROOM_FULL_HZ) - this.grooming) * follow;

    // Eat what the extended proboscis is touching. A drop it has stood over for a while without
    // the brain choosing to feed stops being interesting.
    for (const drop of this.drops) {
      if (!this.atMouth(drop)) continue;
      if (this.extension > 0.5) drop.amount -= dtMs / MEAL_MS;
      else if ((drop.waitedMs = (drop.waitedMs ?? 0) + dtMs) > PATIENCE_MS) drop.ignored = true;
    }
    this.drops = this.drops.filter((drop) => drop.amount > 0.02);

    // Everything below is hand-written walking: wander, go to a nearby drop, keep off the walls.
    const seconds = dtMs / 1000;
    let speed = (1 - Math.max(this.extension, this.grooming)) ** 2;

    const nearest = this.drops
      .filter((drop) => !drop.ignored)
      .map((drop) => ({ drop, distance: Math.hypot(drop.x - this.x, drop.y - this.y) }))
      .filter((d) => d.distance < 3.5)
      .sort((a, b) => a.distance - b.distance)[0];
    const margin = 0.8;
    const nearWall = this.x < margin || this.x > ARENA.width - margin || this.y < margin || this.y > ARENA.height - margin;
    const goal = nearest ? nearest.drop : nearWall ? { x: ARENA.width / 2, y: ARENA.height / 2 } : null;

    if (goal) {
      // Aim from the middle of the body, because that is what the fly turns about. Aiming from
      // the head makes it swing its head in circles around anything close beside it.
      const error = this.headingError(goal);
      this.heading += Math.max(-1, Math.min(1, error * 6)) * TURN_RADIANS_PER_S * seconds;
      const facing = Math.max(0, Math.cos(error)) ** 2;

      if (nearest) {
        // Where the body should be for the head to sit just inside the drop's edge. Short of
        // that it keeps coming, even while reaching with its proboscis; past it, it backs up.
        const gap = nearest.distance - (HEAD_FORWARD + this.dropRadius(nearest.drop) * 0.6);
        if (Math.abs(gap) < 0.03 && facing > 0.9) speed = 0;
        else if (this.grooming < 0.5) speed = Math.sign(gap) * Math.max(speed, 0.3) * facing * Math.min(1, 0.2 + Math.abs(gap));
      } else {
        speed *= facing;
      }
    } else {
      this.wander += (Math.sin(this.timeMs / 230) + Math.sin(this.timeMs / 610 + 1.3)) * 0.9 * seconds;
      this.heading += this.wander * seconds * 2 * speed;
    }

    const stride = WALK_LENGTHS_PER_S * seconds * speed;
    this.x = Math.max(margin / 2, Math.min(ARENA.width - margin / 2, this.x + Math.cos(this.heading) * stride));
    this.y = Math.max(margin / 2, Math.min(ARENA.height - margin / 2, this.y + Math.sin(this.heading) * stride));
    this.gait += stride * 9;
  }

  private atMouth(drop: Drop): boolean {
    return Math.hypot(drop.x - this.headX, drop.y - this.headY) - this.dropRadius(drop) < MOUTH_REACH;
  }

  /** How far the fly would have to turn to point its body at the target. */
  private headingError(target: { x: number; y: number }): number {
    const wanted = Math.atan2(target.y - this.y, target.x - this.x);
    return Math.atan2(Math.sin(wanted - this.heading), Math.cos(wanted - this.heading));
  }
}
