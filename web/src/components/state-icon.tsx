import type { ComponentProps } from "react";
import { Icon } from "./icons";

type IconName = ComponentProps<typeof Icon>["name"];

export function StateIcon({
  active,
  from,
  to
}: {
  active: boolean;
  from: IconName;
  to: IconName;
}) {
  return (
    <span className="state-icon" data-active={active} aria-hidden="true">
      <span className="state-icon-before">
        <Icon name={from} />
      </span>
      <span className="state-icon-after">
        <Icon name={to} />
      </span>
    </span>
  );
}
