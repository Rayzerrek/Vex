import { Link } from "@tanstack/react-router";
import { homePath } from "../routes";

export function Brand() {
  return (
    <Link
      className="inline-flex items-center gap-2 text-2xl font-medium tracking-[-0.06em]"
      to={homePath}
      aria-label="Vex home"
    >
      <svg
        className="size-7"
        viewBox="0 0 48 48"
        fill="none"
        aria-hidden="true"
      >
        <rect width="48" height="48" rx="12" fill="currentColor" />
        <path
          d="M9 11h8l10 26h-8L9 11Zm23 0h8l-5 13h-8l5-13Zm-6 16h8l-4 10h-8l4-10Z"
          fill="var(--paper)"
        />
      </svg>
      <span>vex</span>
    </Link>
  );
}
