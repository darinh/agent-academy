export function taskStatusColor(status: string): "active" | "review" | "done" | "cancel" | "warn" | "muted" {
  switch (status) {
    case "Active":
    case "InProgress": return "active";
    case "Review": return "review";
    case "Completed":
    case "Merged": return "done";
    case "Cancelled":
    case "Rejected": return "cancel";
    case "Blocked": return "warn";
    default: return "muted";
  }
}
