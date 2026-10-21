import { useEffect, useRef, useState } from "react";
import { StateIcon } from "../../components/state-icon";

export function CodeBlock({
  language,
  children
}: {
  language: string;
  children: string;
}) {
  const [status, setStatus] = useState<"idle" | "copied" | "error">("idle");
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
    },
    []
  );
  async function copy() {
    try {
      await navigator.clipboard.writeText(children);
      setStatus("copied");
    } catch {
      setStatus("error");
    }
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => setStatus("idle"), 2400);
  }
  return (
    <div className="code-block">
      <div className="code-toolbar">
        <span>{language}</span>
        <button
          className="copy-button"
          type="button"
          onClick={copy}
          aria-label={`Copy ${language} code`}
          data-status={status}
        >
          <StateIcon active={status === "copied"} from="copy" to="check" />
          <span aria-live="polite">
            {status === "copied"
              ? "Copied"
              : status === "error"
                ? "Select to copy"
                : "Copy"}
          </span>
        </button>
      </div>
      <pre className="doc-code">
        <code>{children}</code>
      </pre>
    </div>
  );
}
