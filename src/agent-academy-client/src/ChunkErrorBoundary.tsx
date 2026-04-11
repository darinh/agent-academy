import { Component, type ReactNode } from "react";
import { Button, MessageBar, MessageBarBody, MessageBarTitle } from "@fluentui/react-components";

interface Props { children: ReactNode }
interface State { hasError: boolean }

/** Catches chunk-load failures from React.lazy and offers a retry. */
export default class ChunkErrorBoundary extends Component<Props, State> {
  state: State = { hasError: false };

  static getDerivedStateFromError(): State {
    return { hasError: true };
  }

  private handleRetry = () => {
    this.setState({ hasError: false });
  };

  render() {
    if (this.state.hasError) {
      return (
        <div style={{ padding: "2rem", textAlign: "center" }}>
          <MessageBar intent="error">
            <MessageBarBody>
              <MessageBarTitle>Failed to load panel</MessageBarTitle>
              A code chunk failed to load — this usually means a network hiccup or a stale deployment.
            </MessageBarBody>
          </MessageBar>
          <Button appearance="primary" onClick={this.handleRetry} style={{ marginTop: "1rem" }}>
            Retry
          </Button>
          <Button appearance="subtle" onClick={() => window.location.reload()} style={{ marginTop: "1rem", marginLeft: "0.5rem" }}>
            Reload page
          </Button>
        </div>
      );
    }
    return this.props.children;
  }
}
