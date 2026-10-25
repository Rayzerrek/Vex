import { useEffect, useState } from "react";
import { SunIcon } from "@phosphor-icons/react/dist/csr/Sun";
import { MoonIcon } from "@phosphor-icons/react/dist/csr/Moon";

export function ThemeToggle() {
  const [dark, setDark] = useState(false);
  useEffect(
    () => setDark(document.documentElement.classList.contains("dark")),
    []
  );
  function toggleTheme() {
    const next = !dark;
    setDark(next);
    document.documentElement.classList.toggle("dark", next);
    document
      .querySelector('meta[name="theme-color"]')
      ?.setAttribute("content", next ? "#181a18" : "#f5f5f4");
    try {
      localStorage.setItem("vex-theme", next ? "dark" : "light");
    } catch {
      /* Storage is optional. */
    }
  }
  return (
    <button
      className="icon-button theme-toggle"
      type="button"
      onClick={toggleTheme}
      aria-label={dark ? "Switch to light theme" : "Switch to dark theme"}
      aria-pressed={dark}
    >
      <span className="icon-swap" data-active={dark} aria-hidden="true">
        <MoonIcon />
        <SunIcon />
      </span>
    </button>
  );
}
