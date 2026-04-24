import { approveAll } from "@github/copilot-sdk";
import { joinSession } from "@github/copilot-sdk/extension";

const DANGEROUS_COMMANDS = [
  { pattern: /rm\s+-rf\s+\/(?!\w)/i, reason: "Recursive delete from root" },
  { pattern: /Remove-Item\s+[A-Z]:\\\s*-Recurse/i, reason: "Recursive delete of drive root" },
  { pattern: /DROP\s+(DATABASE|TABLE)\s/i, reason: "Destructive database operation" },
  { pattern: /git\s+push\s+.*--force\s+(origin\s+)?(main|master|production)/i, reason: "Force push to protected branch" },
  { pattern: /mkfs\./i, reason: "Filesystem format command" },
];

const SECRET_PATTERNS = [
  { pattern: /(?:AKIA|ABIA|ACCA|ASIA)[0-9A-Z]{16}/g, type: "AWS Access Key" },
  { pattern: /ghp_[a-zA-Z0-9]{36}/g, type: "GitHub PAT" },
  { pattern: /gho_[a-zA-Z0-9]{36}/g, type: "GitHub OAuth Token" },
  { pattern: /sk-[a-zA-Z0-9]{20}T3BlbkFJ[a-zA-Z0-9]{20}/g, type: "OpenAI API Key" },
  { pattern: /xox[bpors]-[0-9]{10,13}-[a-zA-Z0-9-]+/g, type: "Slack Token" },
  { pattern: /-----BEGIN (RSA |EC )?PRIVATE KEY-----/g, type: "Private Key" },
  { pattern: /(?:password|passwd|pwd)\s*[:=]\s*["'][^"']{8,}["']/gi, type: "Hardcoded Password" },
];

const session = await joinSession({
  onPermissionRequest: approveAll,
  hooks: {
    onSessionStart: async () => ({
      additionalContext:
        "[repo-shield] Security extension active. " +
        "Never hardcode secrets. Use environment variables for all credentials.",
    }),
    onPreToolUse: async (input) => {
      if (input.toolName === "powershell") {
        const cmd = String(input.toolArgs?.command || "");
        for (const { pattern, reason } of DANGEROUS_COMMANDS) {
          if (pattern.test(cmd)) {
            return {
              permissionDecision: "deny",
              permissionDecisionReason: `[repo-shield] BLOCKED: ${reason}.\nCommand: ${cmd}`,
            };
          }
        }
      }

      if (input.toolName === "create" || input.toolName === "edit") {
        const content = String(input.toolArgs?.file_text || input.toolArgs?.new_str || "");
        const detected = [];
        for (const { pattern, type } of SECRET_PATTERNS) {
          pattern.lastIndex = 0;
          if (pattern.test(content)) detected.push(type);
        }
        if (detected.length > 0) {
          return {
            permissionDecision: "deny",
            permissionDecisionReason:
              `[repo-shield] BLOCKED: Potential secrets detected:\n` +
              detected.map((s) => `  - ${s}`).join("\n") +
              `\nUse environment variables instead.`,
          };
        }
      }
    },
  },
  tools: [],
});