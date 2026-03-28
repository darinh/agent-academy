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
    cursor: "pointer",
    ...shorthands.padding("4px", "8px"),
    ...shorthands.borderRadius("8px"),
    border: "none",
    backgroundColor: "transparent",
    color: "inherit",
    "&:hover": {
      backgroundColor: "rgba(255, 255, 255, 0.06)",
    },
  },
  avatar: {
    width: "28px",
    height: "28px",
    ...shorthands.borderRadius("50%"),
  },
  name: {
    fontSize: "13px",
    color: "#dbe7fb",
    fontWeight: 500,
  },
});

interface UserBadgeProps {
  user: AuthUser;
  onLogout: () => void;
  onOpenSettings?: () => void;
}

export default function UserBadge({ user, onLogout, onOpenSettings }: UserBadgeProps) {
  const s = useLocalStyles();

  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <button className={s.trigger} aria-label="User menu">
          {user.avatarUrl && (
            <img src={user.avatarUrl} alt="" className={s.avatar} />
          )}
          <span className={s.name}>{user.name ?? user.login}</span>
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
