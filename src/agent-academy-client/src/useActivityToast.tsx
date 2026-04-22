import { useCallback } from "react";
import type { ReactElement } from "react";
import { Toast, ToastTitle, ToastBody } from "@fluentui/react-components";
import type { ActivityEvent } from "./api";
import { TOAST_EVENT_TYPES, toastIntent } from "./appConstants";
import type { useDesktopNotifications } from "./useDesktopNotifications";

type DispatchToast = (content: ReactElement, options?: { intent?: "success" | "warning" | "error" | "info"; timeout?: number }) => void;

type DesktopNotif = ReturnType<typeof useDesktopNotifications>;

/**
 * Builds an activity handler that:
 *  - Forwards every event to desktop notifications (the hook decides if it shows).
 *  - Surfaces a Fluent UI toast for events in {@link TOAST_EVENT_TYPES}, with
 *    intent-derived timeout (errors linger 8s, others 4s).
 */
export function useActivityToast(
  dispatchToast: DispatchToast,
  desktopNotif: DesktopNotif,
) {
  return useCallback((evt: ActivityEvent) => {
    desktopNotif.notify(evt);
    if (!TOAST_EVENT_TYPES.has(evt.type)) return;
    const intent = toastIntent(evt);
    const timeout = intent === "error" ? 8000 : 4000;
    dispatchToast(
      <Toast>
        <ToastTitle>{evt.type.replace(/([A-Z])/g, " $1").trim()}</ToastTitle>
        <ToastBody>{evt.message}</ToastBody>
      </Toast>,
      { intent, timeout },
    );
  }, [dispatchToast, desktopNotif]);
}
