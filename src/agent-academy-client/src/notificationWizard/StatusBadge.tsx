import { Spinner } from "@fluentui/react-components";
import {
  CheckmarkCircle24Filled,
  DismissCircle24Filled,
} from "@fluentui/react-icons";

export type ConnectionStatus = "idle" | "loading" | "success" | "error";

export function StatusBadge({ status }: { status: ConnectionStatus }) {
  if (status === "idle") return null;

  return (
    <span className="wizard-status" data-status={status}>
      {status === "loading" && <Spinner size="tiny" />}
      {status === "success" && <CheckmarkCircle24Filled />}
      {status === "error" && <DismissCircle24Filled />}
      {status === "loading" && "Working…"}
      {status === "success" && "Done"}
      {status === "error" && "Failed"}
    </span>
  );
}
