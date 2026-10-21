export function Icon({
  name
}: {
  name:
    | "sun"
    | "moon"
    | "download"
    | "search"
    | "close"
    | "menu"
    | "external"
    | "copy"
    | "check";
}) {
  const paths = {
    sun: "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8ZM12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5",
    moon: "M20 15.2A8.5 8.5 0 0 1 8.8 4 8.5 8.5 0 1 0 20 15.2Z",
    download: "M12 3v12m-5-5 5 5 5-5M5 16v5h14v-5",
    search: "M10.5 4a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13ZM16 16l5 5",
    close: "m6 6 12 12M6 18 18 6",
    menu: "M4 6h16M4 12h16M4 18h16",
    external: "M8 5h11v11M19 5 5 19",
    copy: "M9 9h12v12H9zM15 9V3H3v12h6",
    check: "m5 12 4 4L19 6"
  };
  return (
    <svg
      width="20"
      height="20"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={paths[name]} />
    </svg>
  );
}

export function Mark() {
  return (
    <svg
      className="brand-mark"
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
  );
}
