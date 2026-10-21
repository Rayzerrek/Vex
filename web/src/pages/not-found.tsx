export function NotFound() {
  return (
    <main className="not-found page-width" id="main-content">
      <h1>That page isn’t here.</h1>
      <p>Find what you need in the documentation, or head back home.</p>
      <div className="hero-actions">
        <a className="button button-primary" href="/docs/getting-started">
          Open the docs
        </a>
        <a className="button button-secondary" href="/">
          Back to home
        </a>
      </div>
    </main>
  );
}
