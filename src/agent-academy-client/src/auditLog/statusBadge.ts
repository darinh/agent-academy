import type { BadgeColor } from "../V3Badge";

export function statusBadge(
  status: string,
): { color: BadgeColor; label: string } {
  switch (status) {
    case "Success":
      return { color: "ok", label: "Success" };
    case "Error":
      return { color: "err", label: "Error" };
    case "Denied":
      return { color: "warn", label: "Denied" };
    case "Pending":
      return { color: "info", label: "Pending" };
    default:
      return { color: "bug", label: status };
  }
}
