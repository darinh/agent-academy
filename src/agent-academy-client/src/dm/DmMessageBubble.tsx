import { memo } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { mergeClasses } from "@fluentui/react-components";
import { roleColor, formatRole } from "../theme";
import { formatTime } from "../utils";
import type { DmMessage } from "../api";
import { useDmPanelStyles } from "./dmPanelStyles";

const DmMessageBubble = memo(function DmMessageBubble({
  message,
}: {
  message: DmMessage;
}) {
  const s = useDmPanelStyles();
  const isHuman = message.isFromHuman;
  const isConsultant = isHuman && message.senderRole === "Consultant";
  const consultantColors = isConsultant ? roleColor("Consultant") : null;

  return (
    <div className={mergeClasses(s.msgRow, isHuman && s.msgRowHuman)}>
      <div>
        <div className={mergeClasses(
          s.msgBubble,
          isHuman && !isConsultant && s.msgBubbleHuman,
          isConsultant && s.msgBubbleConsultant,
        )}>
          {!isHuman && (
            <div className={s.msgMeta}>
              <span style={{ fontWeight: 600, fontSize: "13px" }}>
                {message.senderName}
              </span>
            </div>
          )}
          {isConsultant && (
            <div className={s.msgMeta}>
              <span style={{ fontWeight: 600, fontSize: "13px" }}>
                {message.senderName}
              </span>
              <span
                style={{
                  fontSize: "10px",
                  fontWeight: 600,
                  padding: "1px 6px",
                  borderRadius: "4px",
                  backgroundColor: consultantColors!.accent + "26",
                  color: consultantColors!.accent,
                }}
              >
                {formatRole("Consultant")}
              </span>
            </div>
          )}
          <div className={s.msgContent}>
            <Markdown remarkPlugins={[remarkGfm]}>{message.content}</Markdown>
          </div>
        </div>
        <div className={mergeClasses(s.msgTime, isHuman && s.msgTimeHuman)}>
          {formatTime(message.sentAt)}
        </div>
      </div>
    </div>
  );
});

export default DmMessageBubble;
