import { Link } from "@tanstack/react-router";
import { docsPath, homePath } from "../routes";

export function NotFound() {
  return (
    <main
      className="not-found page-width page-enter"
      id="main-content"
      tabIndex={-1}
    >
      <h1>That page isn’t here.</h1>
      <p>Find what you need in the documentation, or head back home.</p>
      <div className="flex flex-wrap gap-3">
        <Link className="button button-primary" to={docsPath}>
          Open the docs
        </Link>
        <Link className="button button-secondary" to={homePath}>
          Back to home
        </Link>
      </div>
    </main>
  );
}
