import { makeStyles, shorthands, tokens } from "@fluentui/react-components";

export const useLayoutStyles = makeStyles({
  root: {
    minHeight: "100vh",
    color: "var(--aa-text)",
    background: "var(--aa-bg)",
  },
  errorBar: {
    position: "sticky",
    top: tokens.spacingVerticalM,
    zIndex: 2,
    ...shorthands.padding(tokens.spacingVerticalM, tokens.spacingHorizontalL, "0"),
  },
  shell: {
    display: "flex",
    height: "100vh",
    width: "100vw",
    overflow: "hidden",
  },
  shellOpen: {
    /* sidebar visible — default */
  },
  shellCollapsed: {
    /* sidebar collapsed — narrower sidebar handled by sidebar class */
  },
});
