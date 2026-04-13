export function roleLabel(role: string): string {
  const map: Record<string, string> = {
    Planner: "Planner",
    Architect: "Architect",
    SoftwareEngineer: "Engineer",
    Reviewer: "Reviewer",
    TechnicalWriter: "Writer",
  };
  return map[role] ?? role;
}

/** Parse a quota input string. Returns null for empty/whitespace, a valid number, or NaN on bad input. */
export function parseQuotaInt(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const n = Number(trimmed);
  if (!Number.isFinite(n) || !Number.isInteger(n) || n < 0) return NaN;
  return n;
}

export function parseQuotaFloat(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const n = Number(trimmed);
  if (!Number.isFinite(n) || n < 0) return NaN;
  return n;
}
