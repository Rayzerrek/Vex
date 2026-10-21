import { useEffect, useRef } from "react";

export function useScrollReveal() {
  const rootRef = useRef<HTMLElement>(null);

  useEffect(() => {
    const root = rootRef.current;
    const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
    if (!root || reducedMotion.matches || !("IntersectionObserver" in window))
      return;

    const groups = Array.from(
      root.querySelectorAll<HTMLElement>("[data-scroll-reveal]")
    );
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          const group = entry.target as HTMLElement;
          observer.unobserve(group);
          group.dataset.scrollState = "visible";
        }
      },
      { rootMargin: "0px 0px -40px 0px", threshold: 0 }
    );

    // Only arm content below the viewport; restored scroll positions must not flash.
    for (const group of groups) {
      if (group.getBoundingClientRect().top < window.innerHeight) continue;
      group.dataset.scrollState = "pending";
      observer.observe(group);
    }

    function revealImmediately() {
      observer.disconnect();
      for (const group of groups) delete group.dataset.scrollState;
    }
    function onMotionChange() {
      if (reducedMotion.matches) revealImmediately();
    }
    function onHashChange() {
      if (document.documentElement.dataset.input !== "pointer")
        revealImmediately();
    }
    function onFocus(event: FocusEvent) {
      if (!(event.target instanceof Element)) return;
      const group = event.target.closest<HTMLElement>("[data-scroll-reveal]");
      if (!group) return;
      observer.unobserve(group);
      delete group.dataset.scrollState;
    }

    // Pointer anchor scrolling keeps reveals active; focus reveals only its own group.
    document.addEventListener("keydown", revealImmediately, true);
    root.addEventListener("focusin", onFocus);
    window.addEventListener("hashchange", onHashChange);
    reducedMotion.addEventListener("change", onMotionChange);
    return () => {
      revealImmediately();
      document.removeEventListener("keydown", revealImmediately, true);
      root.removeEventListener("focusin", onFocus);
      window.removeEventListener("hashchange", onHashChange);
      reducedMotion.removeEventListener("change", onMotionChange);
    };
  }, []);

  return rootRef;
}
