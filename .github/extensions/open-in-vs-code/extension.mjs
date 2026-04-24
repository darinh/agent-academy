import { exec } from "node:child_process";
import { approveAll } from "@github/copilot-sdk";
import { joinSession } from "@github/copilot-sdk/extension";

function openInEditor(filePath) {
  exec(`code "${filePath}"`, () => {});
}

const session = await joinSession({
  onPermissionRequest: approveAll,
  hooks: {
    onPostToolUse: async (input) => {
      if (input.toolName === "create" || input.toolName === "edit") {
        const filePath = input.toolArgs?.path;
        if (filePath) openInEditor(String(filePath));
      }
    },
  },
  tools: [],
});

await session.log("Auto-opener ready — files will open in VS Code");