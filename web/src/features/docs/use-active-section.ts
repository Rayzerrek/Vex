import { useEffect, useState } from "react";

export function useActiveSection() {
  const [active, setActive] = useState("");
  useEffect(() => {
    const sections = Array.from(
      document.querySelectorAll<HTMLElement>(".doc-section")
    );
    if (!sections.length) return;
    let frame = 0;
    function update() {
      frame = 0;
      const threshold = 150;
      const current =
        sections
          .filter((section) => section.getBoundingClientRect().top <= threshold)
          .at(-1) ?? sections[0];
      setActive(current.id);
    }
    function schedule() {
      if (!frame) frame = requestAnimationFrame(update);
    }
    update();
    window.addEventListener("scroll", schedule, { passive: true });
    window.addEventListener("resize", schedule);
    return () => {
      window.removeEventListener("scroll", schedule);
      window.removeEventListener("resize", schedule);
      cancelAnimationFrame(frame);
    };
  }, []);
  return active;
}
