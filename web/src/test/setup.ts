import "@testing-library/react";

// The router scrolls to top on navigation; jsdom has no layout engine, so the
// call is a no-op that would otherwise log a stack trace per navigation.
window.scrollTo = () => {};

// jsdom does not implement matchMedia, which the theme bootstrap in
// index.html and useTheme both consult. The site treats an absent preference
// as light, so the stub reports no match unless a test overrides it.
if (!window.matchMedia) {
  window.matchMedia = (query: string): MediaQueryList =>
    ({
      matches: false,
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false
    }) as MediaQueryList;
}
