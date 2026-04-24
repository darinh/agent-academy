import { execFile } from "node:child_process";
import { approveAll } from "@github/copilot-sdk";
import { joinSession } from "@github/copilot-sdk/extension";

const isWindows = process.platform === "win32";

function copyToClipboard(text) {
  const cmd = isWindows ? "clip" : "pbcopy";
  const proc = execFile(cmd, [], () => {});
  proc.stdin.write(text);
  proc.stdin.end();
}

const session = await joinSession({
  onPermissionRequest: approveAll,
  hooks: {
    onUserPromptSubmitted: async (input) => {
      if (/\bcopy\b/i.test(input.prompt)) {
        return {
          additionalContext:
            "[clipboard] The user wants content copied. Use the copy_to_clipboard tool for the relevant output.",
        };
      }
    },
  },
  tools: [
    {
      name: "copy_to_clipboard",
      description: "Copies text to the system clipboard",
      parameters: {
        type: "object",
        properties: {
          text: { type: "string", description: "Text to copy" },
        },
        required: ["text"],
      },
      handler: async (args) => {
        return new Promise((resolve) => {
          const proc = execFile(isWindows ? "clip" : "pbcopy", [], (err) => {
            if (err) resolve(`Error: ${err.message}`);
            else resolve("Copied to clipboard.");
          });
          proc.stdin.write(args.text);
          proc.stdin.end();
        });
      },
    },
  ],
});