/**
 * Discord bot permissions for Send Messages, Embed Links,
 * Read Message History, Use Slash Commands.
 */
export const BOT_PERMISSIONS = (
  (1n << 11n) | // Send Messages
  (1n << 14n) | // Embed Links
  (1n << 16n) | // Read Message History
  (1n << 31n)   // Use Slash Commands (APPLICATION_COMMANDS)
).toString();

export const TOTAL_STEPS = 3; // Instructions → Credentials → Connect & Test
