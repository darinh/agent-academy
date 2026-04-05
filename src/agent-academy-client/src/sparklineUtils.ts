/**
 * Buckets timestamped records into equal-width time intervals for sparkline display.
 * Returns an array of counts, one per bucket, ordered oldest → newest.
 */
export function bucketByTime<T>(
  records: T[],
  getTimestamp: (record: T) => string,
  bucketCount: number,
  hoursBack?: number,
): number[] {
  if (bucketCount <= 0) return [];
  if (records.length === 0) return new Array(bucketCount).fill(0);

  const now = Date.now();

  // Parse and validate timestamps
  const validRecords: { record: T; ts: number }[] = [];
  for (const record of records) {
    const ts = new Date(getTimestamp(record)).getTime();
    if (Number.isFinite(ts)) validRecords.push({ record, ts });
  }
  if (validRecords.length === 0) return new Array(bucketCount).fill(0);

  // Determine the time window
  let windowStart: number;
  if (hoursBack != null && hoursBack > 0) {
    windowStart = now - hoursBack * 3600_000;
  } else {
    const earliest = Math.min(...validRecords.map((r) => r.ts));
    windowStart = earliest;
  }

  const windowMs = now - windowStart;
  if (windowMs <= 0) return new Array(bucketCount).fill(0);

  const bucketWidth = windowMs / bucketCount;
  const buckets = new Array<number>(bucketCount).fill(0);

  for (const { ts } of validRecords) {
    if (ts < windowStart || ts > now) continue;
    const idx = Math.min(
      Math.floor((ts - windowStart) / bucketWidth),
      bucketCount - 1,
    );
    buckets[idx]++;
  }

  return buckets;
}

/**
 * Aggregates a numeric value per bucket instead of counting records.
 */
export function bucketByTimeSum<T>(
  records: T[],
  getTimestamp: (record: T) => string,
  getValue: (record: T) => number,
  bucketCount: number,
  hoursBack?: number,
): number[] {
  if (bucketCount <= 0) return [];
  if (records.length === 0) return new Array(bucketCount).fill(0);

  const now = Date.now();

  // Parse and validate timestamps
  const validRecords: { record: T; ts: number }[] = [];
  for (const record of records) {
    const ts = new Date(getTimestamp(record)).getTime();
    if (Number.isFinite(ts)) validRecords.push({ record, ts });
  }
  if (validRecords.length === 0) return new Array(bucketCount).fill(0);

  let windowStart: number;
  if (hoursBack != null && hoursBack > 0) {
    windowStart = now - hoursBack * 3600_000;
  } else {
    const earliest = Math.min(...validRecords.map((r) => r.ts));
    windowStart = earliest;
  }

  const windowMs = now - windowStart;
  if (windowMs <= 0) return new Array(bucketCount).fill(0);

  const bucketWidth = windowMs / bucketCount;
  const buckets = new Array<number>(bucketCount).fill(0);

  for (const { record, ts } of validRecords) {
    if (ts < windowStart || ts > now) continue;
    const idx = Math.min(
      Math.floor((ts - windowStart) / bucketWidth),
      bucketCount - 1,
    );
    buckets[idx] += getValue(record);
  }

  return buckets;
}
