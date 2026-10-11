import {
  AmbientLight,
  BufferGeometry,
  Float32BufferAttribute,
  LineBasicMaterial,
  LineSegments,
  Color,
  CylinderGeometry,
  DirectionalLight,
  DoubleSide,
  Group,
  Mesh,
  MeshStandardMaterial,
  PerspectiveCamera,
  PCFSoftShadowMap,
  PlaneGeometry,
  Plane,
  Raycaster,
  Scene,
  SphereGeometry,
  Vector2,
  Vector3,
  WebGLRenderer,
} from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import type { Palette } from "./canvas";
import { ARENA, type Drop, type FlyWorld } from "./flyWorld";

// Body proportions, in body lengths. X is forward, Y is up, Z is the fly's right.
const RIDE_HEIGHT = 0.2;
const HEAD = new Vector3(0.3, RIDE_HEIGHT + 0.01, 0);
const LEG_ATTACH_X = [0.14, 0.03, -0.08];
const FOOT_REST = [new Vector3(0.36, 0, 0.3), new Vector3(0.04, 0, 0.4), new Vector3(-0.32, 0, 0.32)];
const STRIDE = 0.09;
const STEP_LIFT = 0.07;

/** A straight limb segment that can be stretched between any two points. */
class Segment {
  readonly mesh: Mesh;
  private static readonly up = new Vector3(0, 1, 0);
  private static readonly span = new Vector3();

  constructor(radius: number, material: MeshStandardMaterial) {
    this.mesh = new Mesh(new CylinderGeometry(radius, radius * 0.8, 1, 8), material);
    this.mesh.castShadow = true;
  }

  between(from: Vector3, to: Vector3): void {
    const span = Segment.span.subVectors(to, from);
    const length = span.length();
    this.mesh.position.copy(from).addScaledVector(span, 0.5);
    this.mesh.scale.set(1, Math.max(length, 1e-4), 1);
    this.mesh.quaternion.setFromUnitVectors(Segment.up, span.normalize());
  }
}

type Leg = { side: number; pair: number; upper: Segment; lower: Segment; foot: Mesh };

/**
 * The fly and its arena in three dimensions. It only draws what FlyWorld says is happening;
 * it decides nothing. Drag to look around, scroll to zoom, click the ground to drop food.
 */
export class FlyView {
  readonly supported: boolean;

  private renderer: WebGLRenderer | null = null;
  private readonly scene = new Scene();
  private readonly camera = new PerspectiveCamera(32, 1, 0.1, 100);
  private controls: OrbitControls | null = null;

  private readonly fly = new Group();
  private readonly body = new Group();
  private readonly legs: Leg[] = [];
  private readonly antennae: Mesh[] = [];
  private readonly wings: Mesh[] = [];
  private readonly proboscis: Segment;
  private readonly labellum: Mesh;
  private readonly dropMeshes: Mesh[] = [];
  private readonly ground: Mesh;

  private readonly skin = new MeshStandardMaterial({ roughness: 0.75 });
  private readonly dark = new MeshStandardMaterial({ roughness: 0.8 });
  private readonly eye = new MeshStandardMaterial({ roughness: 0.35 });
  private readonly limb = new MeshStandardMaterial({ roughness: 0.7 });
  private readonly mouth = new MeshStandardMaterial({ roughness: 0.5 });
  private readonly feeler = new MeshStandardMaterial({ roughness: 0.6 });
  private readonly wing = new MeshStandardMaterial({ transparent: true, opacity: 0.42, side: DoubleSide, roughness: 0.2, depthWrite: false });
  private readonly floor = new MeshStandardMaterial({ roughness: 1 });
  private readonly gridLine = new LineBasicMaterial({ transparent: true, opacity: 0.5 });
  private readonly sugar = new MeshStandardMaterial({ roughness: 0.4 });
  private readonly water = new MeshStandardMaterial({ roughness: 0.1, transparent: true, opacity: 0.8 });
  private readonly dropGeometry = new SphereGeometry(1, 24, 12, 0, Math.PI * 2, 0, Math.PI / 2);

  constructor(
    private readonly canvas: HTMLCanvasElement,
    private palette: Palette,
    private readonly world: FlyWorld,
    onGroundClick: (x: number, y: number) => void,
  ) {
    this.ground = new Mesh(new PlaneGeometry(ARENA.width, ARENA.height), this.floor);
    this.proboscis = new Segment(0.022, this.mouth);
    this.labellum = new Mesh(new SphereGeometry(0.035, 12, 8), this.mouth);

    try {
      this.renderer = new WebGLRenderer({ canvas, antialias: true });
    } catch {
      this.supported = false;
      return;
    }
    this.supported = true;
    this.renderer.shadowMap.enabled = true;
    this.renderer.shadowMap.type = PCFSoftShadowMap;

    this.buildScene();
    this.buildFly();
    this.setPalette(palette);

    this.controls = new OrbitControls(this.camera, canvas);
    this.controls.target.set(world.x, 0.1, world.y);
    this.camera.position.set(world.x + 1.6, 2.1, world.y + 3.3);
    this.controls.maxPolarAngle = Math.PI * 0.47;
    this.controls.minDistance = 1.2;
    this.controls.maxDistance = 18;
    this.controls.enablePan = false;
    this.controls.update();

    new ResizeObserver(() => this.resize()).observe(canvas);
    this.resize();
    this.listenForClicks(onGroundClick);
  }

  setPalette(palette: Palette): void {
    this.palette = palette;
    if (!this.supported) return;
    const ground = new Color(palette.surface);
    const dark = ground.getHSL({ h: 0, s: 0, l: 0 }).l < 0.5;
    this.scene.background = ground;
    this.floor.color.set(dark ? "#22272b" : "#e4e7e8");
    this.gridLine.color.set(dark ? "#3a4147" : "#c9ced1");
    // A fruit fly's own colours: tan thorax, banded abdomen, red eyes.
    this.skin.color.set("#8f7a5c");
    this.dark.color.set("#3b332b");
    this.limb.color.set("#4a4037");
    this.eye.color.set(palette.excitatory);
    this.mouth.color.set(palette.output);
    this.wing.color.set(dark ? "#cfd6dc" : "#6f777d");
    this.sugar.color.set(palette.sugar);
    this.water.color.set(palette.water);
  }

  private buildScene(): void {
    this.scene.add(new AmbientLight(0xffffff, 1.5));
    const sun = new DirectionalLight(0xffffff, 2.4);
    sun.position.set(ARENA.width / 2 - 3, 8, ARENA.height / 2 - 2);
    sun.target.position.set(ARENA.width / 2, 0, ARENA.height / 2);
    sun.castShadow = true;
    sun.shadow.mapSize.set(2048, 2048);
    const reach = Math.max(ARENA.width, ARENA.height) * 0.75;
    Object.assign(sun.shadow.camera, { left: -reach, right: reach, top: reach, bottom: -reach, near: 1, far: 20 });
    this.scene.add(sun, sun.target);

    this.ground.rotation.x = -Math.PI / 2;
    this.ground.position.set(ARENA.width / 2, 0, ARENA.height / 2);
    this.ground.receiveShadow = true;

    // A line every body length, so movement across the floor is visible.
    const lines: number[] = [];
    for (let x = 0; x <= ARENA.width; x++) lines.push(x, 0.002, 0, x, 0.002, ARENA.height);
    for (let z = 0; z <= ARENA.height; z++) lines.push(0, 0.002, z, ARENA.width, 0.002, z);
    const grid = new BufferGeometry();
    grid.setAttribute("position", new Float32BufferAttribute(lines, 3));
    this.scene.add(this.ground, new LineSegments(grid, this.gridLine), this.fly);
  }

  private buildFly(): void {
    const part = (geometry: BufferGeometry, material: MeshStandardMaterial, x: number, y: number, z: number, scale: [number, number, number]) => {
      const mesh = new Mesh(geometry, material);
      mesh.position.set(x, y, z);
      mesh.scale.set(...scale);
      mesh.castShadow = true;
      this.body.add(mesh);
      return mesh;
    };
    const ball = new SphereGeometry(1, 24, 16);

    part(ball, this.skin, 0.06, RIDE_HEIGHT + 0.03, 0, [0.16, 0.13, 0.13]);
    part(ball, this.skin, -0.25, RIDE_HEIGHT, 0, [0.26, 0.12, 0.13]);
    for (const x of [-0.13, -0.24, -0.35]) part(ball, this.dark, x, RIDE_HEIGHT + 0.004, 0, [0.022, 0.122, 0.132]);
    part(ball, this.skin, HEAD.x, HEAD.y, 0, [0.1, 0.1, 0.12]);
    for (const side of [-1, 1]) part(ball, this.eye, HEAD.x + 0.015, HEAD.y + 0.02, side * 0.075, [0.07, 0.075, 0.06]);

    for (const side of [-1, 1]) {
      const antenna = part(new CylinderGeometry(0.012, 0.018, 0.09, 8), this.feeler, HEAD.x + 0.1, HEAD.y + 0.045, side * 0.035, [1, 1, 1]);
      antenna.rotation.z = -1.0;
      antenna.rotation.y = -side * 0.35;
      this.antennae.push(antenna);
    }

    // Wings: thin translucent blades lying back over the abdomen, splayed slightly apart.
    for (const side of [-1, 1]) {
      const wing = new Mesh(ball, this.wing);
      wing.position.set(-0.26, RIDE_HEIGHT + 0.135, side * 0.07);
      wing.scale.set(0.3, 0.004, 0.085);
      wing.rotation.set(side * 0.1, -side * 0.16, 0.04);
      this.body.add(wing);
      this.wings.push(wing);
    }

    this.body.add(this.proboscis.mesh, this.labellum);
    this.labellum.castShadow = true;

    for (const side of [-1, 1]) {
      for (let pair = 0; pair < 3; pair++) {
        const foot = new Mesh(new SphereGeometry(0.014, 8, 6), this.limb);
        const leg: Leg = { side, pair, upper: new Segment(0.016, this.limb), lower: new Segment(0.012, this.limb), foot };
        this.body.add(leg.upper.mesh, leg.lower.mesh, foot);
        this.legs.push(leg);
      }
    }
    this.fly.add(this.body);
  }

  private listenForClicks(onGroundClick: (x: number, y: number) => void): void {
    const raycaster = new Raycaster();
    const floor = new Plane(new Vector3(0, 1, 0), 0);
    const hit = new Vector3();
    let downAt: { x: number; y: number } | null = null;

    this.canvas.addEventListener("pointerdown", (event) => (downAt = { x: event.clientX, y: event.clientY }));
    this.canvas.addEventListener("pointerup", (event) => {
      // A drag turns the camera; only a press and release in one spot counts as a click.
      if (!downAt || Math.hypot(event.clientX - downAt.x, event.clientY - downAt.y) > 5) return;
      const box = this.canvas.getBoundingClientRect();
      const point = new Vector2(((event.clientX - box.left) / box.width) * 2 - 1, -((event.clientY - box.top) / box.height) * 2 + 1);
      raycaster.setFromCamera(point, this.camera);
      if (raycaster.ray.intersectPlane(floor, hit)) onGroundClick(hit.x, hit.z);
    });
  }

  private resize(): void {
    const { width, height } = this.canvas.getBoundingClientRect();
    if (!this.renderer || width === 0 || height === 0) return;
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    this.renderer.setSize(width, height, false);
    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();
  }

  private readonly attach = new Vector3();
  private readonly knee = new Vector3();
  private readonly footAt = new Vector3();
  private readonly tip = new Vector3();
  private readonly follow = new Vector3();

  private poseLegs(): void {
    const { gait, grooming, timeMs } = this.world;
    for (const leg of this.legs) {
      const { side, pair } = leg;
      // Two tripods: each leg moves with the diagonal ones and against its neighbours.
      const phase = gait + ((pair + (side > 0 ? 1 : 0)) % 2 === 0 ? 0 : Math.PI);
      const rest = FOOT_REST[pair];
      this.footAt.set(rest.x + Math.cos(phase) * STRIDE, Math.max(0, Math.sin(phase)) * STEP_LIFT, side * rest.z);
      this.attach.set(LEG_ATTACH_X[pair], RIDE_HEIGHT - 0.04, side * 0.08);

      if (pair === 0 && grooming > 0.02) {
        // Front legs come up and rub over the antennae.
        const rub = Math.sin(timeMs / 18 + (side > 0 ? 0 : 1.4)) * 0.035;
        const target = this.tip.set(HEAD.x + 0.13 + rub, HEAD.y + 0.03 + rub * 0.5, side * 0.035);
        this.footAt.lerp(target, grooming);
      }

      this.knee.addVectors(this.attach, this.footAt).multiplyScalar(0.5);
      this.knee.y += 0.13;
      this.knee.z += side * 0.07;
      leg.upper.between(this.attach, this.knee);
      leg.lower.between(this.knee, this.footAt);
      leg.foot.position.copy(this.footAt);
    }
  }

  private poseMouth(): void {
    const { extension } = this.world;
    const base = this.attach.set(HEAD.x + 0.05, HEAD.y - 0.07, 0);
    // Folded under the head at rest; reaching forward and down to the ground when extended.
    this.tip.set(HEAD.x + 0.05 + 0.13 * extension, HEAD.y - 0.09 - (HEAD.y - 0.12) * extension, 0);
    this.proboscis.between(base, this.tip);
    this.labellum.position.copy(this.tip);
    this.labellum.scale.setScalar(0.6 + 0.6 * extension);
  }

  private syncDrops(drops: Drop[]): void {
    while (this.dropMeshes.length < drops.length) {
      const mesh = new Mesh(this.dropGeometry, this.sugar);
      mesh.castShadow = true;
      this.scene.add(mesh);
      this.dropMeshes.push(mesh);
    }
    this.dropMeshes.forEach((mesh, i) => {
      const drop = drops[i];
      mesh.visible = drop !== undefined;
      if (!drop) return;
      const radius = this.world.dropRadius(drop);
      mesh.material = drop.kind === "sugar" ? this.sugar : this.water;
      mesh.position.set(drop.x, 0, drop.y);
      mesh.scale.set(radius, radius * 0.55, radius);
    });
  }

  render(): void {
    if (!this.renderer || this.canvas.hidden) return;
    const world = this.world;
    this.fly.position.set(world.x, 0, world.y);
    this.fly.rotation.y = -world.heading;
    this.body.position.y = Math.abs(Math.sin(world.gait * 2)) * 0.006;

    this.poseLegs();
    this.poseMouth();
    this.syncDrops(world.drops);
    this.feeler.color.set(world.touched ? this.palette.inhibitory : "#4a4037");
    for (const antenna of this.antennae) antenna.scale.setScalar(world.touched ? 1.5 : 1);

    // The camera keeps its angle and distance but drifts along with the fly.
    if (this.controls) {
      this.follow.set(world.x, 0.1, world.y).sub(this.controls.target).multiplyScalar(0.06);
      this.controls.target.add(this.follow);
      this.camera.position.add(this.follow);
      this.controls.update();
    }
    this.renderer.render(this.scene, this.camera);
  }
}
