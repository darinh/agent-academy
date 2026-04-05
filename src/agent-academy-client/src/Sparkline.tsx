import { memo, useId } from "react";

interface SparklineProps {
  data: number[];
  width?: number;
  height?: number;
  color?: string;
  fillOpacity?: number;
  strokeWidth?: number;
  className?: string;
}

/**
 * Minimal SVG sparkline — a smooth polyline with an optional gradient fill.
 * No axes, no labels, no interactivity. Just a trend line.
 */
const Sparkline = memo(function Sparkline({
  data,
  width = 120,
  height = 32,
  color = "#6cb6ff",
  fillOpacity = 0.15,
  strokeWidth = 1.5,
  className,
}: SparklineProps) {
  if (data.length < 2) return null;

  const max = Math.max(...data);
  const min = Math.min(...data);
  const range = max - min || 1; // avoid division by zero

  const padY = strokeWidth + 1; // vertical padding so stroke doesn't clip
  const plotH = height - padY * 2;
  const stepX = width / (data.length - 1);

  const points = data.map((v, i) => {
    const x = i * stepX;
    const y = padY + plotH - ((v - min) / range) * plotH;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  });

  const polyline = points.join(" ");
  const instanceId = useId();
  const fillId = `sparkfill-${instanceId.replace(/:/g, "")}`;

  // Close the path along the bottom for the fill area
  const fillPoints = `${points.join(" ")} ${width.toFixed(1)},${height.toFixed(1)} 0,${height.toFixed(1)}`;

  return (
    <svg
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      className={className}
      role="img"
      aria-label="Sparkline trend"
      style={{ display: "block", flexShrink: 0 }}
    >
      <defs>
        <linearGradient id={fillId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity={fillOpacity} />
          <stop offset="100%" stopColor={color} stopOpacity={0} />
        </linearGradient>
      </defs>
      <polygon
        points={fillPoints}
        fill={`url(#${fillId})`}
      />
      <polyline
        points={polyline}
        fill="none"
        stroke={color}
        strokeWidth={strokeWidth}
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
});

export default Sparkline;
