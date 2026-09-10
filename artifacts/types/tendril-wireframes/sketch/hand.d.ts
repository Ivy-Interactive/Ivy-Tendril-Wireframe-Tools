export type Pt = [number, number];
/**
 * A repeatable hand. Same seed, same wobble, every render — the sketch layer's
 * `deterministic` promise extends to the marks built on top of it.
 */
export declare function handRng(seed: string | number | undefined): () => number;
/** An SVG path for a rounded rectangle. Falls back to a plain box at radius 0. */
export declare function roundedRectPath(x: number, y: number, w: number, h: number, r: number): string;
/**
 * Samples a quadratic from `from` to `to` whose control point is pushed
 * `bend` pixels off the perpendicular. A bend of 0 gives a straight run, which
 * rough.js then draws with its own wobble.
 */
export declare function bendPoints(from: Pt, to: Pt, bend: number, steps?: number): Pt[];
