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

// jsdom has no `DOMMatrixReadOnly`. The library's list motion reads a row's transform through it when rows
// change, so a spec whose list fills after the first render needs an identity answer.
if (!('DOMMatrixReadOnly' in globalThis)) {
  class IdentityMatrix {
    public readonly m41 = 0;
    public readonly m42 = 0;
  }
  Object.defineProperty(globalThis, 'DOMMatrixReadOnly', {
    writable: true,
    configurable: true,
    value: IdentityMatrix,
  });
}

// The offline cache keeps the list and the waiting edits in `localStorage`, and a worker can run several spec files in
// one jsdom window. Each test starts with an empty device, so what one spec leaves there is never read by the next.
beforeEach(() => localStorage.clear());
