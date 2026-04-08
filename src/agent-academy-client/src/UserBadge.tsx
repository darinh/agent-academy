import {
  makeStyles,
  shorthands,
  Menu,
  MenuTrigger,
  MenuPopover,
  MenuList,
  MenuItem,
} from "@fluentui/react-components";
import {
  SettingsRegular,
  SignOutRegular,
} from "@fluentui/react-icons";
import type { AuthUser } from "./api";

const useLocalStyles = makeStyles({
  trigger: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    width: "100%",
    cursor: "pointer",
    border: "none",
    borderTop: "1px solid var(--aa-border)",
    background: "transparent",
    color: "var(--aa-soft)",
    ...shorthands.padding("8px", "14px"),
    fontSize: "11px",
    ":hover": {
      color: "var(--aa-text)",
      background: "rgba(91, 141, 239, 0.04)",
    },
  },
  avatar: {
    width: "22px",
    height: "22px",
    display: "block",
    ...shorthands.borderRadius("50%"),
    flexShrink: 0,
  },
  fallbackAvatar: {
    width: "22px",
    height: "22px",
    display: "grid",
    placeItems: "center",
    color: "white",
    fontSize: "9px",
    fontWeight: 700,
    background: "linear-gradient(135deg, var(--primary), var(--special))",
    ...shorthands.borderRadius("50%"),
    flexShrink: 0,
  },
  meta: {
    display: "flex",
    alignItems: "center",
    gap: "4px",
    minWidth: 0,
    flex: 1,
  },
  label: {
    display: "none",
  },
  name: {
    color: "inherit",
    fontSize: "11px",
    fontWeight: 500,
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
  },
});

interface UserBadgeProps {
  user: AuthUser;
  onLogout: () => void;
  onOpenSettings?: () => void;
}

export default function UserBadge({ user, onLogout, onOpenSettings }: UserBadgeProps) {
  const s = useLocalStyles();
  const name = user.name ?? user.login;
  const initials = name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? "")
    .join("") || "AA";

  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <button className={s.trigger} aria-label="User menu" type="button">
          {user.avatarUrl ? (
            <img src={user.avatarUrl} alt="" className={s.avatar} />
          ) : (
            <span className={s.fallbackAvatar}>{initials}</span>
          )}
          <span className={s.meta}>
            <span className={s.label}>GitHub</span>
            <span className={s.name}>{name}</span>
          </span>
        </button>
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          {onOpenSettings && (
            <MenuItem icon={<SettingsRegular />} onClick={onOpenSettings}>
              Settings
            </MenuItem>
          )}
          <MenuItem icon={<SignOutRegular />} onClick={onLogout}>
            Sign out
          </MenuItem>
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}
