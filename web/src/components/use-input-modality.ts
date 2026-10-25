import { useEffect } from "react";

export function useInputModality() {
  useEffect(() => {
    const root = document.documentElement;
    function keyboard(event: KeyboardEvent) {
      if (!event.metaKey && !event.ctrlKey && !event.altKey)
        root.dataset.input = "keyboard";
    }
    function pointer() {
      root.dataset.input = "pointer";
    }
    window.addEventListener("keydown", keyboard, true);
    window.addEventListener("pointerdown", pointer, true);
    return () => {
      window.removeEventListener("keydown", keyboard, true);
      window.removeEventListener("pointerdown", pointer, true);
    };
  }, []);
}
