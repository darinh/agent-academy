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
    display: "inline-flex",
    alignItems: "center",
    gap: "10px",
    cursor: "pointer",
    border: "1px solid rgba(174, 192, 222, 0.14)",
    background:
      "linear-gradient(180deg, rgba(255, 255, 255, 0.05), rgba(255, 255, 255, 0.02))",
    color: "inherit",
    boxShadow: "0 18px 36px rgba(0, 0, 0, 0.22)",
    transitionDuration: "140ms",
    transitionProperty: "border-color, background-color, transform",
    ...shorthands.padding("7px", "11px", "7px", "7px"),
    ...shorthands.borderRadius("999px"),
    ":hover": {
      transform: "translateY(-1px)",
      border: "1px solid rgba(131, 207, 255, 0.24)",
      background: "linear-gradient(180deg, rgba(131, 207, 255, 0.09), rgba(255, 255, 255, 0.03))",
    },
  },
  avatar: {
    width: "34px",
    height: "34px",
    display: "block",
    boxShadow: "0 6px 16px rgba(0, 0, 0, 0.24)",
    ...shorthands.borderRadius("999px"),
  },
  fallbackAvatar: {
    width: "34px",
    height: "34px",
    display: "grid",
    placeItems: "center",
    color: "#101722",
    fontSize: "12px",
    fontWeight: 800,
    background: "linear-gradient(145deg, #f3d4a8, #83cfff)",
    boxShadow: "0 6px 16px rgba(0, 0, 0, 0.24)",
    ...shorthands.borderRadius("999px"),
  },
  meta: {
    display: "grid",
    gap: "2px",
    minWidth: 0,
  },
  label: {
    color: "#93a8c9",
    fontSize: "10px",
    fontWeight: 700,
    letterSpacing: "0.12em",
    textTransform: "uppercase",
  },
  name: {
    color: "#f4f8ff",
    fontSize: "13px",
    fontWeight: 700,
    maxWidth: "180px",
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
