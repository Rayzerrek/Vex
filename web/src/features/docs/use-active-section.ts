import { useEffect, useState } from "react";

export function useActiveSection() {
  const [active, setActive] = useState("");
  useEffect(() => {
    const sections = Array.from(
      document.querySelectorAll<HTMLElement>(".doc-section")
    );
    if (!sections.length) return;
    let frame = 0;
    let requested = window.location.hash.slice(1);
    function update() {
      frame = 0;
      const threshold = 150;
      const atBottom =
        window.innerHeight + window.scrollY >=
        document.documentElement.scrollHeight - 2;
      const requestedSection = sections.find(
        (section) => section.id === requested
      );
      const current =
        ((atBottom && requestedSection) ||
          (atBottom && sections.at(-1)) ||
          sections
            .filter(
              (section) => section.getBoundingClientRect().top <= threshold
            )
            .at(-1)) ??
        sections[0];
      setActive(current.id);
    }
    function schedule() {
      if (!frame) frame = requestAnimationFrame(update);
    }
    function onHashChange() {
      requested = window.location.hash.slice(1);
      schedule();
    }
    function onManualScroll() {
      requested = "";
    }
    update();
    window.addEventListener("hashchange", onHashChange);
    window.addEventListener("wheel", onManualScroll, { passive: true });
    window.addEventListener("touchstart", onManualScroll, { passive: true });
    window.addEventListener("keydown", onManualScroll);
    window.addEventListener("scroll", schedule, { passive: true });
    window.addEventListener("resize", schedule);
    return () => {
      window.removeEventListener("hashchange", onHashChange);
      window.removeEventListener("wheel", onManualScroll);
      window.removeEventListener("touchstart", onManualScroll);
      window.removeEventListener("keydown", onManualScroll);
      window.removeEventListener("scroll", schedule);
      window.removeEventListener("resize", schedule);
      cancelAnimationFrame(frame);
    };
  }, []);
  return active;
}
