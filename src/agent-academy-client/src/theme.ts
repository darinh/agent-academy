export type AvatarColor =
  | "brand"
  | "dark-red"
  | "cranberry"
  | "pumpkin"
  | "forest"
  | "grape"
  | "lavender"
  | "navy"
  | "colorful";

export interface RoleTheme {
  accent: string;
  foreground: string;
  avatar: AvatarColor;
}

export const ROLE_COLORS: Record<string, RoleTheme> = {
  Planner:          { accent: "#b794ff", foreground: "#ffffff", avatar: "lavender" },
  Architect:        { accent: "#ffbe70", foreground: "#1a1208", avatar: "pumpkin" },
  SoftwareEngineer: { accent: "#48d67a", foreground: "#08210f", avatar: "forest" },
  Reviewer:         { accent: "#ff7187", foreground: "#ffffff", avatar: "cranberry" },
  Validator:        { accent: "#d6a0ff", foreground: "#ffffff", avatar: "grape" },
  TechnicalWriter:  { accent: "#7dd3fc", foreground: "#09111f", avatar: "colorful" },
  Writer:           { accent: "#7dd3fc", foreground: "#09111f", avatar: "colorful" },
  Human:            { accent: "#6cb6ff", foreground: "#ffffff", avatar: "brand" },
  Consultant:       { accent: "#e0976e", foreground: "#1a1208", avatar: "pumpkin" },
};

const DEFAULT_ROLE: RoleTheme = { accent: "#94a3b8", foreground: "#ffffff", avatar: "colorful" };

export function roleColor(role?: string | null): RoleTheme {
  return ROLE_COLORS[role ?? ""] ?? DEFAULT_ROLE;
}

export function formatRole(role?: string | null): string {
  if (!role) return "Room stream";
  return role.replace(/([a-z])([A-Z])/g, "$1 $2");
}
