import { describe, expect, it } from "vitest";
import { bucketByTime, bucketByTimeSum } from "../sparklineUtils";

interface MockRecord {
  timestamp: string;
  value: number;
}

function makeRecord(minutesAgo: number, value = 1): MockRecord {
  const ts = new Date(Date.now() - minutesAgo * 60_000).toISOString();
  return { timestamp: ts, value };
}

describe("sparklineUtils", () => {
  describe("bucketByTime", () => {
    it("returns empty array for zero buckets", () => {
      const records = [makeRecord(5)];
      expect(bucketByTime(records, (r) => r.timestamp, 0)).toEqual([]);
    });

    it("returns zero-filled array for empty records", () => {
      expect(bucketByTime([], (r: MockRecord) => r.timestamp, 4)).toEqual([0, 0, 0, 0]);
    });

    it("buckets records into correct time slots with hoursBack", () => {
      // 4 buckets over 4 hours = 1 hour per bucket
      const records = [
        makeRecord(210), // 3.5 hours ago → bucket 0
        makeRecord(150), // 2.5 hours ago → bucket 1
        makeRecord(90),  // 1.5 hours ago → bucket 2
        makeRecord(30),  // 0.5 hours ago → bucket 3
      ];
      const result = bucketByTime(records, (r) => r.timestamp, 4, 4);
      expect(result).toEqual([1, 1, 1, 1]);
    });

    it("puts multiple records in the same bucket", () => {
      const records = [
        makeRecord(10), // very recent
        makeRecord(15), // very recent
        makeRecord(20), // very recent
      ];
      // 2 buckets over 1 hour — all should be in the last bucket
      const result = bucketByTime(records, (r) => r.timestamp, 2, 1);
      expect(result[1]).toBe(3);
      expect(result[0]).toBe(0);
    });

    it("ignores records outside the time window", () => {
      const records = [
        makeRecord(300), // 5 hours ago — outside 2-hour window
        makeRecord(30),  // 30 min ago — inside
      ];
      const result = bucketByTime(records, (r) => r.timestamp, 4, 2);
      const total = result.reduce((a, b) => a + b, 0);
      expect(total).toBe(1);
    });

    it("auto-ranges when hoursBack is not set", () => {
      const records = [
        makeRecord(60),
        makeRecord(30),
      ];
      const result = bucketByTime(records, (r) => r.timestamp, 4);
      const total = result.reduce((a, b) => a + b, 0);
      expect(total).toBe(2);
    });

    it("handles single record", () => {
      const records = [makeRecord(30)];
      const result = bucketByTime(records, (r) => r.timestamp, 4, 1);
      const total = result.reduce((a, b) => a + b, 0);
      expect(total).toBe(1);
    });

    it("skips records with invalid timestamps", () => {
      const records = [
        { timestamp: "not-a-date", value: 1 },
        { timestamp: "", value: 1 },
        makeRecord(30),
      ];
      const result = bucketByTime(records, (r) => r.timestamp, 4, 1);
      const total = result.reduce((a, b) => a + b, 0);
      expect(total).toBe(1);
    });

    it("returns zeros when all timestamps are invalid", () => {
      const records = [
        { timestamp: "invalid", value: 1 },
        { timestamp: "also-invalid", value: 1 },
      ];
      const result = bucketByTime(records, (r) => r.timestamp, 4, 1);
      expect(result).toEqual([0, 0, 0, 0]);
    });
  });

  describe("bucketByTimeSum", () => {
    it("sums values within each bucket", () => {
      const records = [
        makeRecord(10, 100),
        makeRecord(12, 200),
        makeRecord(14, 50),
      ];
      // All recent — should all be in the last bucket(s)
      const result = bucketByTimeSum(records, (r) => r.timestamp, (r) => r.value, 2, 1);
      const total = result.reduce((a, b) => a + b, 0);
      expect(total).toBe(350);
    });

    it("returns zeros for empty records", () => {
      const result = bucketByTimeSum(
        [] as MockRecord[],
        (r) => r.timestamp,
        (r) => r.value,
        4,
      );
      expect(result).toEqual([0, 0, 0, 0]);
    });

    it("returns empty array for zero buckets", () => {
      const records = [makeRecord(5, 42)];
      expect(bucketByTimeSum(records, (r) => r.timestamp, (r) => r.value, 0)).toEqual([]);
    });

    it("distributes sums across time buckets", () => {
      // 4 buckets over 4 hours
      const records = [
        makeRecord(210, 10),  // bucket 0
        makeRecord(150, 20),  // bucket 1
        makeRecord(90, 30),   // bucket 2
        makeRecord(30, 40),   // bucket 3
      ];
      const result = bucketByTimeSum(records, (r) => r.timestamp, (r) => r.value, 4, 4);
      expect(result).toEqual([10, 20, 30, 40]);
    });
  });
});
