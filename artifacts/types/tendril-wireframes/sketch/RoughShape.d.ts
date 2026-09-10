import { RoughOptions, SketchShape } from './useRough';
import * as React from "react";
export interface RoughShapeProps {
    shape: SketchShape;
    stroke?: string | "none";
    strokeWidth?: number;
    fill?: string;
    fillStyle?: RoughOptions["fillStyle"];
    fillWeight?: number;
    hachureAngle?: number;
    hachureGap?: number;
    roughness?: number;
    bowing?: number;
    seed?: string | number;
    opacity?: number;
    strokeLineDash?: number[];
    className?: string;
    onClick?: React.MouseEventHandler<SVGGElement>;
    onMouseEnter?: React.MouseEventHandler<SVGGElement>;
    onMouseLeave?: React.MouseEventHandler<SVGGElement>;
}
/**
 * Draws a rough.js shape inside an existing `<svg>`. Used by the charts and by
 * any widget that needs sketch geometry rather than a sketch border.
 */
export declare const RoughShape: ({ shape, stroke, strokeWidth, fill, fillStyle, fillWeight, hachureAngle, hachureGap, roughness, bowing, seed, opacity, strokeLineDash, className, ...handlers }: RoughShapeProps) => React.JSX.Element;
