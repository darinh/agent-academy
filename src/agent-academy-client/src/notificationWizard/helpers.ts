export function getStepTitle(providerId: string, step: number): string {
  const name = getProviderDisplayName(providerId);
  switch (step) {
    case 1: return `Set Up ${name}`;
    case 2: return "Enter Credentials";
    case 3: return "Connect & Test";
    default: return "";
  }
}

export function getProviderDisplayName(providerId: string): string {
  switch (providerId) {
    case "discord": return "Discord";
    case "slack": return "Slack";
    default: return providerId.charAt(0).toUpperCase() + providerId.slice(1);
  }
}
