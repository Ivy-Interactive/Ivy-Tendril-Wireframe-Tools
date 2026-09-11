import { useCallback, useEffect, useState } from "react";

export type ThemeChoice = "system" | "light" | "dark";
export type ResolvedTheme = "light" | "dark";

const STORAGE_KEY = "wireframe-studio-theme";
const CHOICES: ThemeChoice[] = ["system", "light", "dark"];

function readStored(): ThemeChoice {
  try {
    const value = localStorage.getItem(STORAGE_KEY);
    if (value === "light" || value === "dark" || value === "system") return value;
  } catch {
    // Private browsing, or storage disabled. Following the OS is the right default.
  }
  return "system";
}

const prefersLight = () =>
  typeof matchMedia === "function" && matchMedia("(prefers-color-scheme: light)").matches;

export function resolve(choice: ThemeChoice): ResolvedTheme {
  if (choice === "system") return prefersLight() ? "light" : "dark";
  return choice;
}

/**
 * Writes the resolved theme onto <html>.
 *
 * "system" is resolved here rather than with a prefers-color-scheme block in the CSS, so
 * the stylesheet needs one light palette instead of two copies of it.
 */
function apply(theme: ResolvedTheme) {
  document.documentElement.dataset.theme = theme;
}

/**
 * Applied before React mounts, from an inline script in the page head. Without it the
 * first paint uses the dark default and a light-mode user sees a flash of dark chrome.
 */
export function applyStoredThemeEarly() {
  apply(resolve(readStored()));
}

export function useTheme() {
  const [choice, setChoice] = useState<ThemeChoice>(readStored);
  const [resolved, setResolved] = useState<ResolvedTheme>(() => resolve(readStored()));

  useEffect(() => {
    const next = resolve(choice);
    setResolved(next);
    apply(next);

    try {
      localStorage.setItem(STORAGE_KEY, choice);
    } catch {
      // Not worth surfacing; the choice just will not survive a reload.
    }

    // Only follow the OS while the user has actually asked to.
    if (choice !== "system" || typeof matchMedia !== "function") return;

    const query = matchMedia("(prefers-color-scheme: light)");
    const onChange = () => {
      const updated = resolve("system");
      setResolved(updated);
      apply(updated);
    };
    query.addEventListener("change", onChange);
    return () => query.removeEventListener("change", onChange);
  }, [choice]);

  /** Cycles system -> light -> dark -> system. */
  const cycle = useCallback(() => {
    setChoice((current) => CHOICES[(CHOICES.indexOf(current) + 1) % CHOICES.length]);
  }, []);

  return { choice, resolved, setChoice, cycle };
}
