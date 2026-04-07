import type { CSSProperties, ReactNode } from "react";

/**
 * v3 badge — mono, 9px, uppercase, with color-coded backgrounds.
 * Replaces Fluent UI Badge across all views for mockup-accurate rendering.
 */

export type BadgeColor =
  | "active"
  | "review"
  | "done"
  | "cancel"
  | "feat"
  | "bug"
  | "info"
  | "warn"
  | "err"
  | "ok"
  | "muted"
  | "tool";

const COLOR_MAP: Record<BadgeColor, { bg: string; fg: string }> = {
  active: { bg: "rgba(91, 141, 239, 0.15)", fg: "var(--aa-cyan)" },
  review: { bg: "rgba(255, 152, 0, 0.15)", fg: "var(--aa-gold)" },
  done:   { bg: "rgba(76, 175, 80, 0.15)", fg: "var(--aa-lime)" },
  cancel: { bg: "rgba(139, 148, 158, 0.1)", fg: "var(--aa-soft)" },
  feat:   { bg: "rgba(156, 39, 176, 0.12)", fg: "var(--aa-plum)" },
  bug:    { bg: "rgba(232, 93, 93, 0.12)", fg: "var(--aa-copper)" },
  info:   { bg: "rgba(91, 141, 239, 0.1)", fg: "var(--aa-cyan)" },
  warn:   { bg: "rgba(255, 152, 0, 0.1)", fg: "var(--aa-gold)" },
  err:    { bg: "rgba(232, 93, 93, 0.1)", fg: "var(--aa-copper)" },
  ok:     { bg: "rgba(76, 175, 80, 0.1)", fg: "var(--aa-lime)" },
  muted:  { bg: "rgba(139, 148, 158, 0.08)", fg: "var(--aa-soft)" },
  tool:   { bg: "rgba(0, 150, 136, 0.1)", fg: "var(--aa-tool)" },
};

const BASE_STYLE: CSSProperties = {
  fontFamily: "var(--mono)",
  fontSize: "9px",
  fontWeight: 500,
  padding: "2px 6px",
  borderRadius: "3px",
  textTransform: "uppercase",
  letterSpacing: "0.03em",
  whiteSpace: "nowrap",
  lineHeight: 1,
  display: "inline-block",
};

interface V3BadgeProps {
  children: ReactNode;
  color: BadgeColor;
  className?: string;
  style?: CSSProperties;
}

export default function V3Badge({ children, color, className, style }: V3BadgeProps) {
  const c = COLOR_MAP[color];
  return (
    <span
      className={className}
      style={{ ...BASE_STYLE, background: c.bg, color: c.fg, ...style }}
    >
      {children}
    </span>
  );
}
