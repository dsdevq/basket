// Runs before the Angular bundle (a plain ES5 script, so even a very old browser runs it). If the bundle never
// starts - it fails to load, fails to parse in an older browser, or is blocked - this puts a visible message on
// the page instead of leaving it blank. It shows an error name only: no message, no stack, no URL.
(function () {
  // The one newer API Angular itself calls unguarded at start-up (Chrome < 93, Safari < 15.4, Firefox < 92).
  if (!Object.hasOwn) {
    Object.hasOwn = function (object, key) {
      return Object.prototype.hasOwnProperty.call(object, key);
    };
  }

  var ID = 'bk-crash';
  var WAIT_MS = 15000;
  var done = false;
  var ownPanel = null;

  // Set by the bundle once the first routed page has rendered (crash-screen.ts, MOUNTED_ATTRIBUTE). An empty
  // <router-outlet> in <bk-root> is not enough: a blocked lazy chunk leaves exactly that.
  function mounted() {
    return document.documentElement.hasAttribute('data-bk-mounted');
  }

  function show(name, hint) {
    if (done || mounted() || document.getElementById(ID) || !document.body) return;
    var panel = document.createElement('div');
    ownPanel = panel;
    panel.id = ID;
    panel.setAttribute('role', 'alert');
    panel.style.cssText =
      'position:fixed;top:0;right:0;bottom:0;left:0;z-index:2147483647;padding:24px;text-align:center;' +
      'font:16px/1.4 system-ui,sans-serif;background:#fff;color:#111;display:flex;flex-direction:column;' +
      'align-items:center;justify-content:center';
    var h = document.createElement('h1');
    h.style.cssText = 'margin:0 0 12px;font-size:22px';
    h.appendChild(document.createTextNode('Something went wrong'));
    var d = document.createElement('p');
    d.style.margin = '0 0 12px';
    d.appendChild(
      document.createTextNode(
        'Error: ' +
          String(name)
            .replace(/[^\w .:-]/g, '')
            .slice(0, 60)
      )
    );
    var p = document.createElement('p');
    p.style.cssText = 'margin:0 0 16px;max-width:24em';
    p.appendChild(document.createTextNode(hint));
    var b = document.createElement('button');
    b.type = 'button';
    b.style.cssText = 'font:inherit;padding:10px 24px';
    b.appendChild(document.createTextNode('Reload'));
    b.onclick = function () {
      location.reload();
    };
    panel.appendChild(h);
    panel.appendChild(d);
    panel.appendChild(p);
    panel.appendChild(b);
    document.body.appendChild(panel);
  }

  var HINT_BROKEN =
    'basket could not start in this browser. Try Safari or Chrome, or turn off content blockers.';

  // Capture phase: a failed <script> load does not bubble, a thrown error does.
  window.addEventListener(
    'error',
    function (event) {
      var target = event.target;
      if (target && target !== window && target.tagName === 'SCRIPT') {
        show('ScriptLoadError', HINT_BROKEN);
      } else if (event.error && !mounted()) {
        show(event.error.name || 'Error', HINT_BROKEN);
      }
    },
    true
  );

  var waited = 0;
  var timer = setInterval(function () {
    waited += 500;
    if (mounted()) {
      clearInterval(timer);
      done = true;
      if (ownPanel && ownPanel.parentNode) ownPanel.parentNode.removeChild(ownPanel);
    } else if (waited >= WAIT_MS) {
      show('NotStarted', HINT_BROKEN);
    }
  }, 500);
})();
