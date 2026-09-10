import type * as React from "react";
/** Sizing / spacing scale shared by every widget, mirroring `Ivy.Densities`. */
export type Densities = "Small" | "Medium" | "Large";
export type Orientation = "Horizontal" | "Vertical";
export type Align = "TopLeft" | "TopRight" | "TopCenter" | "BottomLeft" | "BottomRight" | "BottomCenter" | "Left" | "Right" | "Center" | "Stretch" | "SpaceBetween" | "SpaceAround" | "SpaceEvenly";
export type BorderStyle = "None" | "Solid" | "Dashed" | "Dotted";
export type BorderRadius = "None" | "Rounded" | "Full";
export type Overflow = "Clip" | "Ellipsis" | "Auto" | "Visible" | "Scroll";
/** Which axes a container scrolls on, mirroring `Ivy.Scroll`. */
export type Scroll = "None" | "Auto" | "Vertical" | "Horizontal" | "Both";
export type TextAlignment = "Left" | "Center" | "Right" | "Justify";
export type HoverEffect = "None" | "Pointer" | "PointerAndTranslate" | "Shadow";
export type Sizing = number | string;
/**
 * Per-edge spacing, mirroring `Ivy.Thickness`. A bare number is uniform on all
 * four edges; both go through `toCssSize`, so a number means Ivy's 0.25rem unit.
 */
export interface Thickness {
    left?: Sizing;
    top?: Sizing;
    right?: Sizing;
    bottom?: Sizing;
}
/** Item shared by `DropDownMenu`, `Toolbar`, `Tree` and every row-action surface. */
export interface MenuItem {
    label: string;
    icon?: string;
    tag?: string;
    tooltip?: string;
    children?: MenuItem[];
    variant?: "Default" | "Separator" | "Checkbox" | "Radio" | "Group";
    checked?: boolean;
    disabled?: boolean;
    color?: string;
    shortcut?: string;
    badge?: string;
    expanded?: boolean;
    path?: string;
    onSelect?: (item: MenuItem) => void;
}
export interface Option {
    value: string | number;
    label?: string;
    description?: string;
    group?: string;
    icon?: string;
    disabled?: boolean;
    tooltip?: string;
}
export type NullableSelectValue = string | number | string[] | number[] | null | undefined;
export type FileUploadStatus = "Pending" | "Aborted" | "Loading" | "Failed" | "Finished";
export interface FileItem {
    id: string;
    fileName: string;
    contentType: string;
    length: number;
    progress: number;
    status: FileUploadStatus;
}
export interface InternalLink {
    title: string;
    appId: string;
}
/**
 * The props every Ivy widget inherits from `Ivy.WidgetBase`, so every Tendril
 * component accepts them too.
 */
export interface WidgetBaseProps {
    id?: string;
    width?: Sizing;
    height?: Sizing;
    aspectRatio?: number;
    /**
     * Sizing and spacing scale. Everything that reads it goes through
     * `byDensity`, which falls back to `Medium`, so `Medium` is the default
     * everywhere the prop is left off.
     *
     * @default "Medium"
     */
    density?: Densities;
    /** `false` hides the widget without unmounting it, matching Ivy's `Visible`. */
    visible?: boolean;
    className?: string;
    style?: React.CSSProperties;
    "data-testid"?: string;
}
