import { useCallback, useMemo, useState } from "react";
import type { MenuCheckedValueChangeData } from "@fluentui/react-components";
import { loadFilters, saveFilters } from "./chatUtils";
import type { MessageFilter } from "./chatUtils";

export function useChatFilters() {
  const [hiddenFilters, setHiddenFilters] = useState<Set<MessageFilter>>(loadFilters);

  const chatFilterChecked = useMemo(() => {
    const visible: string[] = [];
    if (!hiddenFilters.has("system")) visible.push("system");
    if (!hiddenFilters.has("commands")) visible.push("commands");
    return { show: visible };
  }, [hiddenFilters]);

  const onChatFilterChange = useCallback((_: unknown, data: MenuCheckedValueChangeData) => {
    const nowVisible = new Set(data.checkedItems);
    const next = new Set<MessageFilter>();
    if (!nowVisible.has("system")) next.add("system");
    if (!nowVisible.has("commands")) next.add("commands");
    setHiddenFilters(next);
    saveFilters(next);
  }, []);

  return { hiddenFilters, chatFilterChecked, onChatFilterChange };
}
