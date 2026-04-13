import { BOT_PERMISSIONS } from "./constants";

export function ProviderInstructions({
  providerId,
  appId,
  onAppIdChange,
  inviteUrl,
}: {
  providerId: string;
  appId: string;
  onAppIdChange: (v: string) => void;
  inviteUrl: string;
}) {
  if (providerId === "discord") return <DiscordInstructions appId={appId} onAppIdChange={onAppIdChange} inviteUrl={inviteUrl} />;
  if (providerId === "slack") return <SlackInstructions />;
  return <GenericInstructions />;
}

export function DiscordInstructions({
  appId,
  onAppIdChange,
  inviteUrl,
}: {
  appId: string;
  onAppIdChange: (v: string) => void;
  inviteUrl: string;
}) {
  return (
    <>
      <ol>
        <li>
          Go to the{" "}
          <a
            href="https://discord.com/developers/applications"
            target="_blank"
            rel="noopener noreferrer"
          >
            Discord Developer Portal
          </a>
        </li>
        <li>Click <strong>New Application</strong> and give it a name</li>
        <li>Navigate to <strong>Bot</strong> in the left sidebar</li>
        <li>Click <strong>Reset Token</strong> and copy the token (you'll need it in the next step)</li>
        <li>
          Still on the <strong>Bot</strong> page, scroll to <strong>Privileged Gateway Intents</strong> and
          enable <strong>MESSAGE CONTENT INTENT</strong> — this is required for the bot to read human replies
        </li>
      </ol>

      <p style={{ marginTop: 16 }}>
        <strong>Invite your bot to a server:</strong> Enter your Application ID
        (found under <em>General Information</em>) to generate the invite link.
      </p>

      <div className="wizard-field">
        <label htmlFor="discord-app-id">Application ID</label>
        <input
          id="discord-app-id"
          type="text"
          inputMode="numeric"
          autoComplete="off"
          placeholder="e.g. 110270667856912345"
          value={appId}
          onChange={(e) => onAppIdChange(e.target.value)}
        />
      </div>

      {inviteUrl ? (
        <div className="invite-link-box">
          <a href={inviteUrl} target="_blank" rel="noopener noreferrer">
            {inviteUrl}
          </a>
        </div>
      ) : (
        <p className="wizard-field-hint">Enter your Application ID above to generate the invite link.</p>
      )}
    </>
  );
}

export function SlackInstructions() {
  return (
    <>
      <ol>
        <li>
          Go to{" "}
          <a
            href="https://api.slack.com/apps"
            target="_blank"
            rel="noopener noreferrer"
          >
            api.slack.com/apps
          </a>{" "}
          and click <strong>Create New App</strong> → <strong>From scratch</strong>
        </li>
        <li>Name your app (e.g. &quot;Agent Academy&quot;) and select your workspace</li>
        <li>
          Navigate to <strong>OAuth &amp; Permissions</strong> and add these <strong>Bot Token Scopes</strong>:
          <ul className="scope-list">
            <li><code>chat:write</code> — send messages</li>
            <li><code>channels:manage</code> — create channels</li>
            <li><code>channels:read</code> — list channels</li>
            <li><code>channels:join</code> — join public channels</li>
            <li><code>groups:write</code> — manage private channels</li>
            <li><code>groups:read</code> — list private channels</li>
            <li><code>users:read</code> — resolve user info</li>
          </ul>
        </li>
        <li>Click <strong>Install to Workspace</strong> and authorize</li>
        <li>Copy the <strong>Bot User OAuth Token</strong> (starts with <code>xoxb-</code>)</li>
      </ol>
      <p>
        You'll also need a <strong>Default Channel ID</strong> — right-click any channel
        in Slack → <strong>View channel details</strong> → copy the ID at the bottom.
      </p>
    </>
  );
}

export function GenericInstructions() {
  return (
    <p>
      Follow your provider's documentation to obtain the required credentials,
      then proceed to the next step to enter them.
    </p>
  );
}

export { BOT_PERMISSIONS };
