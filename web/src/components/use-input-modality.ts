import { useEffect } from "react";

// Capture runs before React handlers, so keyboard-triggered state changes stay instant.
export function useInputModality() {
  useEffect(() => {
    const root = document.documentElement;
    function pointer() {
      root.dataset.input = "pointer";
    }
    function keyboard() {
      root.dataset.input = "keyboard";
    }
    document.addEventListener("pointerdown", pointer, true);
    document.addEventListener("keydown", keyboard, true);
    return () => {
      document.removeEventListener("pointerdown", pointer, true);
      document.removeEventListener("keydown", keyboard, true);
      delete root.dataset.input;
    };
  }, []);
}
