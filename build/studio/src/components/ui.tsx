import type { ReactNode } from "react";
import { PanelResizeHandle } from "react-resizable-panels";

export function cx(...parts: (string | false | null | undefined)[]) {
  return parts.filter(Boolean).join(" ");
}

/** A titled region. Every panel in the Studio is one of these, so the chrome stays uniform. */
export function Pane({
  title,
  subtitle,
  actions,
  children,
  className,
  bodyClassName,
}: {
  title?: ReactNode;
  subtitle?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  className?: string;
  bodyClassName?: string;
}) {
  return (
    <section className={cx("flex h-full min-h-0 flex-col bg-shell", className)}>
      {(title || actions) && (
        <header className="flex h-9 shrink-0 items-center gap-2 border-b border-edge px-3">
          <h2 className="truncate text-[11px] font-semibold tracking-wider text-body-muted uppercase">
            {title}
          </h2>
          {subtitle && (
            <span className="truncate font-mono text-[11px] text-body-faint">{subtitle}</span>
          )}
          <div className="ml-auto flex shrink-0 items-center gap-1">{actions}</div>
        </header>
      )}
      <div className={cx("min-h-0 flex-1", bodyClassName)}>{children}</div>
    </section>
  );
}

export function IconButton({
  icon,
  label,
  onClick,
  disabled,
  active,
  tone = "default",
}: {
  icon: ReactNode;
  label: string;
  onClick?: () => void;
  disabled?: boolean;
  active?: boolean;
  tone?: "default" | "brand" | "bad";
}) {
  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      onClick={onClick}
      disabled={disabled}
      className={cx(
        "inline-flex h-7 items-center gap-1.5 rounded px-2 text-xs transition-colors",
        "disabled:cursor-not-allowed disabled:opacity-40",
        active
          ? "bg-edge-bright text-body"
          : "text-body-muted hover:bg-shell-raised hover:text-body",
        tone === "brand" && !active && "text-brand hover:text-brand",
        tone === "bad" && !active && "text-bad hover:text-bad"
      )}
    >
      {icon}
    </button>
  );
}

export function Button({
  children,
  onClick,
  disabled,
  variant = "ghost",
  icon,
}: {
  children: ReactNode;
  onClick?: () => void;
  disabled?: boolean;
  variant?: "ghost" | "primary";
  icon?: ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={cx(
        "inline-flex h-7 shrink-0 items-center gap-1.5 rounded px-2.5 text-xs font-medium transition-colors",
        "disabled:cursor-not-allowed disabled:opacity-40",
        variant === "primary"
          ? "bg-brand text-shell hover:brightness-110"
          : "border border-edge text-body-muted hover:border-edge-bright hover:text-body"
      )}
    >
      {icon}
      {children}
    </button>
  );
}

export function Empty({ children, icon }: { children: ReactNode; icon?: ReactNode }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-2 px-6 text-center">
      {icon && <div className="text-body-faint">{icon}</div>}
      <p className="max-w-xs text-xs leading-relaxed text-body-faint">{children}</p>
    </div>
  );
}

export function Dot({ tone }: { tone: "good" | "warn" | "bad" | "idle" }) {
  return (
    <span
      className={cx(
        "inline-block size-1.5 shrink-0 rounded-full",
        tone === "good" && "bg-good",
        tone === "warn" && "animate-pulse bg-warn",
        tone === "bad" && "bg-bad",
        tone === "idle" && "bg-body-faint"
      )}
    />
  );
}

export function VHandle() {
  return <PanelResizeHandle className="rp-handle rp-handle-v" />;
}

export function HHandle() {
  return <PanelResizeHandle className="rp-handle rp-handle-h" />;
}

export function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

export function formatAgo(iso: string) {
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 45) return "just now";
  if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)}h ago`;
  return `${Math.round(seconds / 86400)}d ago`;
}
