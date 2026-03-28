import { Button, makeStyles, shorthands } from "@fluentui/react-components";
import type { AuthUser } from "./api";

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
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
  logoutBtn: {
    minWidth: "auto",
    fontSize: "12px",
    color: "#7c90b2",
    height: "28px",
  },
});

interface UserBadgeProps {
  user: AuthUser;
  onLogout: () => void;
}

export default function UserBadge({ user, onLogout }: UserBadgeProps) {
  const s = useLocalStyles();

  return (
    <div className={s.root}>
      {user.avatarUrl && (
        <img src={user.avatarUrl} alt="" className={s.avatar} />
      )}
      <span className={s.name}>{user.name ?? user.login}</span>
      <Button
        className={s.logoutBtn}
        appearance="subtle"
        size="small"
        onClick={onLogout}
      >
        Sign out
      </Button>
    </div>
  );
}
