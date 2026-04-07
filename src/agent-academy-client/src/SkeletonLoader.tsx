import { makeStyles, shorthands } from "@fluentui/react-components";

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    gap: "12px",
    ...shorthands.padding("16px", "0"),
  },
  row: {
    display: "flex",
    alignItems: "center",
    gap: "12px",
  },
  circle: {
    width: "36px",
    height: "36px",
    ...shorthands.borderRadius("50%"),
    flexShrink: 0,
  },
  lines: {
    display: "flex",
    flexDirection: "column",
    gap: "8px",
    flex: 1,
  },
  line: {
    height: "12px",
    ...shorthands.borderRadius("6px"),
  },
  shimmer: {
    background: "rgba(196, 173, 141, 0.08)",
    animationName: {
      "0%": { opacity: 0.4 },
      "50%": { opacity: 0.7 },
      "100%": { opacity: 0.4 },
    },
    animationDuration: "1.6s",
    animationIterationCount: "infinite",
    animationTimingFunction: "ease-in-out",
  },
});

interface SkeletonLoaderProps {
  rows?: number;
  variant?: "list" | "chat";
}

export default function SkeletonLoader({ rows = 5, variant = "list" }: SkeletonLoaderProps) {
  const s = useLocalStyles();

  return (
    <div className={s.root}>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className={s.row}>
          {variant === "chat" && (
            <div className={`${s.circle} ${s.shimmer}`} />
          )}
          <div className={s.lines}>
            <div
              className={`${s.line} ${s.shimmer}`}
              style={{ width: `${60 + (i % 3) * 15}%` }}
            />
            <div
              className={`${s.line} ${s.shimmer}`}
              style={{ width: `${40 + ((i + 1) % 4) * 12}%` }}
            />
          </div>
        </div>
      ))}
    </div>
  );
}
