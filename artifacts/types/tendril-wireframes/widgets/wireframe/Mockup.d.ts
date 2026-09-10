import { WidgetBaseProps } from '../../lib/types';
import * as React from "react";
export type MockupVariant = "Mobile" | "Website" | "Tablet" | "Desktop";
export interface WireframeMockupProps extends WidgetBaseProps {
    variant?: MockupVariant;
    color?: string;
    /** Window title on the `Desktop` variant. Ignored by the other variants. */
    title?: string;
    /** Address bar text on the `Website` variant. Ignored by the other variants. */
    url?: string;
    /** Laid out inside the frame's screen area, so a mockup can hold anything. */
    children?: React.ReactNode;
}
/**
 * A hand-drawn device or browser frame wrapped around real content.
 * Mirrors `Ivy.WireframeMockup`.
 *
 * @tags device phone browser frame chrome
 * @slot children Laid out inside the frame's screen area
 * @example <WireframeMockup variant="Mobile"><Card title="Inbox" /></WireframeMockup>
 * @example <WireframeMockup variant="Website" url="app.example.com" width="42rem" />
 */
export declare const WireframeMockup: ({ id, variant, color, title, url, children, density, width, height, aspectRatio, visible, className, style, }: WireframeMockupProps) => React.JSX.Element;
