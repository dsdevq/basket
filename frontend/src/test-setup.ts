// jsdom has no `matchMedia`. The shared `lk-install-hint` asks it whether the app is already installed, and
// the CDK breakpoint observer subscribes to it, so every spec that renders the app shell needs an answer.
// This one answers "no match" to every query.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  configurable: true,
  value: (query: string): MediaQueryList => {
    const list: MediaQueryList = {
      matches: false,
      media: query,
      onchange: null,
      addListener: () => undefined,
      removeListener: () => undefined,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      dispatchEvent: () => false,
    };
    return list;
  },
});
