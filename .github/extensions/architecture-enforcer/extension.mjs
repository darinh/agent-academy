import { approveAll } from "@github/copilot-sdk";
import { joinSession } from "@github/copilot-sdk/extension";

const BOUNDARY_RULES = [
  { from: /^src\/controllers\//, cannotImport: /^src\/database\//, reason: "Controllers must not import directly from database layer. Use services instead." },
  { from: /^src\/shared\//, cannotImport: /^src\/(controllers|routes)\//, reason: "Shared modules cannot depend on controllers or routes." },
  { from: /^src\//, cannotImport: /^\.\.\/\.\.\//, reason: "Deep relative imports are not allowed. Use path aliases." },
];

const session = await joinSession({
  onPermissionRequest: approveAll,
  hooks: {
    onPostToolUse: async (input) => {
      if (input.toolName !== "edit" && input.toolName !== "create") return;
      const filePath = String(input.toolArgs?.path || "").replace(/\\/g, "/");
      const content = String(input.toolArgs?.new_str || input.toolArgs?.file_text || "");

      const imports = content.match(/(?:import|require)\s*\(?['"]([^'"]+)['"]\)?/g) || [];
      const violations = [];

      for (const rule of BOUNDARY_RULES) {
        if (!rule.from.test(filePath)) continue;
        for (const imp of imports) {
          const target = imp.match(/['"]([^'"]+)['"]/)?.[1] || "";
          if (rule.cannotImport.test(target)) {
            violations.push(`${rule.reason} (import: ${target})`);
          }
        }
      }

      if (violations.length > 0) {
        return {
          additionalContext:
            `[arch-enforcer] Architecture violations in ${filePath}:\n` +
            violations.map((v) => `  ⚠ ${v}`).join("\n") +
            `\nFix these before proceeding.`,
        };
      }
    },
  },
  tools: [],
});