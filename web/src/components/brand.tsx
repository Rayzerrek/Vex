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
          d="M 23.2 37.2 C 16 37.2, 10.8 32.8, 10.8 26 C 8.4 25.2, 7.6 22.8, 10 21.2 C 8.8 15.6, 7.6 10, 10 8.4 C 12.4 6.8, 17.2 10.8, 20.4 14.8 C 22.4 14, 25.2 14, 27.6 14.8 C 31.6 10, 36.8 6.4, 38.8 8.4 C 40.8 10.4, 38.8 16.8, 37.2 22 C 38.8 27.6, 34.4 37.2, 23.2 37.2 Z"
          fill="var(--paper)"
        />
        <ellipse cx="18.8" cy="25.2" rx="2.5" ry="3.2" fill="currentColor" />
        <ellipse cx="29.2" cy="25.2" rx="2.5" ry="3.2" fill="currentColor" />
      </svg>
      <span>vex</span>
    </Link>
  );
}
