// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { renderHook, act } from "@testing-library/react";
import type { MenuCheckedValueChangeData } from "@fluentui/react-components";

vi.mock("../chatUtils", () => ({
  loadFilters: vi.fn(),
  saveFilters: vi.fn(),
}));

import { loadFilters, saveFilters } from "../chatUtils";
import { useChatFilters } from "../useChatFilters";

const mockLoadFilters = vi.mocked(loadFilters);
const mockSaveFilters = vi.mocked(saveFilters);

beforeEach(() => {
  vi.clearAllMocks();
  mockLoadFilters.mockReturnValue(new Set());
});

describe("useChatFilters", () => {
  describe("initial state", () => {
    it("calls loadFilters on mount and returns no hidden filters", () => {
      const { result } = renderHook(() => useChatFilters());

      expect(mockLoadFilters).toHaveBeenCalled();
      expect(result.current.hiddenFilters.size).toBe(0);
    });

    it("returns both system and commands as visible when no filters hidden", () => {
      const { result } = renderHook(() => useChatFilters());

      expect(result.current.chatFilterChecked).toEqual({
        show: ["system", "commands"],
      });
    });

    it("initializes with previously saved filters", () => {
      mockLoadFilters.mockReturnValue(new Set(["system"]));

      const { result } = renderHook(() => useChatFilters());

      expect(result.current.hiddenFilters).toEqual(new Set(["system"]));
      expect(result.current.chatFilterChecked).toEqual({
        show: ["commands"],
      });
    });

    it("initializes with both filters hidden", () => {
      mockLoadFilters.mockReturnValue(new Set(["system", "commands"]));

      const { result } = renderHook(() => useChatFilters());

      expect(result.current.chatFilterChecked).toEqual({ show: [] });
    });
  });

  describe("onChatFilterChange", () => {
    it("hides system messages when unchecked", () => {
      const { result } = renderHook(() => useChatFilters());

      act(() => {
        result.current.onChatFilterChange(
          {} as unknown,
          { name: "show", checkedItems: ["commands"] } as MenuCheckedValueChangeData,
        );
      });

      expect(result.current.hiddenFilters).toEqual(new Set(["system"]));
      expect(result.current.chatFilterChecked).toEqual({ show: ["commands"] });
      expect(mockSaveFilters).toHaveBeenCalledWith(new Set(["system"]));
    });

    it("hides commands when unchecked", () => {
      const { result } = renderHook(() => useChatFilters());

      act(() => {
        result.current.onChatFilterChange(
          {} as unknown,
          { name: "show", checkedItems: ["system"] } as MenuCheckedValueChangeData,
        );
      });

      expect(result.current.hiddenFilters).toEqual(new Set(["commands"]));
      expect(result.current.chatFilterChecked).toEqual({ show: ["system"] });
    });

    it("hides both when neither checked", () => {
      const { result } = renderHook(() => useChatFilters());

      act(() => {
        result.current.onChatFilterChange(
          {} as unknown,
          { name: "show", checkedItems: [] } as MenuCheckedValueChangeData,
        );
      });

      expect(result.current.hiddenFilters).toEqual(new Set(["system", "commands"]));
      expect(result.current.chatFilterChecked).toEqual({ show: [] });
    });

    it("shows all when both checked", () => {
      mockLoadFilters.mockReturnValue(new Set(["system", "commands"]));
      const { result } = renderHook(() => useChatFilters());

      act(() => {
        result.current.onChatFilterChange(
          {} as unknown,
          { name: "show", checkedItems: ["system", "commands"] } as MenuCheckedValueChangeData,
        );
      });

      expect(result.current.hiddenFilters.size).toBe(0);
      expect(result.current.chatFilterChecked).toEqual({
        show: ["system", "commands"],
      });
      expect(mockSaveFilters).toHaveBeenCalledWith(new Set());
    });

    it("persists filters on every change", () => {
      const { result } = renderHook(() => useChatFilters());

      act(() => {
        result.current.onChatFilterChange(
          {} as unknown,
          { name: "show", checkedItems: ["system"] } as MenuCheckedValueChangeData,
        );
      });

      act(() => {
        result.current.onChatFilterChange(
          {} as unknown,
          { name: "show", checkedItems: [] } as MenuCheckedValueChangeData,
        );
      });

      expect(mockSaveFilters).toHaveBeenCalledTimes(2);
    });
  });
});
