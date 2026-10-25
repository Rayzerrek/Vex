import { ArrowUpRightIcon } from "@phosphor-icons/react/dist/csr/ArrowUpRight";
import { repository } from "../../routes";

type Section = { id: string; title: string };

function SectionLinks({
  sections,
  active
}: {
  sections: Section[];
  active: string;
}) {
  return sections.map((section) => (
    <a
      key={section.id}
      href={`#${section.id}`}
      aria-current={active === section.id ? "location" : undefined}
    >
      {section.title}
    </a>
  ));
}

export function TableOfContents({
  sections,
  active,
  mobile = false
}: {
  sections: Section[];
  active: string;
  mobile?: boolean;
}) {
  if (mobile)
    return (
      <details className="mobile-toc">
        <summary>On this page</summary>
        <SectionLinks sections={sections} active={active} />
      </details>
    );
  return (
    <aside className="docs-toc">
      <h2>On this page</h2>
      <nav aria-label="On this page">
        <SectionLinks sections={sections} active={active} />
      </nav>
      <a className="toc-help" href={`${repository}/issues`}>
        Something missing?<span>Let us know on GitHub.</span>
        <ArrowUpRightIcon aria-hidden="true" />
      </a>
    </aside>
  );
}
