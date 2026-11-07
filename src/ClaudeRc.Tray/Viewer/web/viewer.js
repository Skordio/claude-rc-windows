// The console viewer page (SPEC 3.3). It only talks to the app through chrome.webview messages
// (ViewerProtocol.cs): it never reaches the network, and the host never sees this page.
//   page -> app: ready {cols, rows}, input {data}, resize {cols, rows}, copy {text}
//   app -> page: output {data: base64 bytes}, status {text|null, level}, focus
(function () {
  'use strict';

  const webview = window.chrome && window.chrome.webview;
  const post = (msg) => { if (webview) webview.postMessage(msg); };

  const term = new Terminal({
    cursorBlink: true,
    scrollback: 5000,
    fontFamily: '"Cascadia Mono", Consolas, "Courier New", monospace',
    fontSize: 14,
    theme: { background: '#1e1e1e', foreground: '#e6e6e6' },
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(document.getElementById('terminal'));
  const banner = document.getElementById('banner');

  const doFit = () => {
    try {
      fit.fit();
    } catch (e) {
      // Not laid out yet (zero size); the next resize fits again.
    }
  };
  doFit();

  // Bytes, not text: xterm decodes UTF-8 itself and keeps a sequence split across two writes.
  const decodeBase64 = (b64) => {
    const s = atob(b64);
    const bytes = new Uint8Array(s.length);
    for (let i = 0; i < s.length; i++) bytes[i] = s.charCodeAt(i);
    return bytes;
  };

  const setBanner = (text, level) => {
    if (text) {
      banner.textContent = text;
      banner.className = level === 'error' ? 'error' : '';
      banner.hidden = false;
    } else {
      banner.hidden = true;
      banner.textContent = '';
    }
    doFit();
  };

  term.attachCustomKeyEventHandler((e) => {
    if (e.type !== 'keydown' || !e.ctrlKey || e.altKey) return true;
    const key = e.key.toLowerCase();
    // Ctrl+C copies while text is selected; otherwise it goes to the session (it is ^C there).
    if (key === 'c' && (e.shiftKey || term.hasSelection())) {
      if (term.hasSelection()) post({ type: 'copy', text: term.getSelection() });
      term.clearSelection();
      return false;
    }
    // Ctrl+V and Ctrl+Shift+V paste (the browser's paste event reaches xterm, which sends it as input).
    if (key === 'v') return false;
    return true;
  });

  term.onData((data) => post({ type: 'input', data: data }));
  term.onResize((size) => post({ type: 'resize', cols: size.cols, rows: size.rows }));

  let fitTimer = 0;
  new ResizeObserver(() => {
    clearTimeout(fitTimer);
    fitTimer = setTimeout(doFit, 80);
  }).observe(document.getElementById('terminal'));

  if (webview) {
    webview.addEventListener('message', (e) => {
      const m = e.data;
      if (!m || typeof m !== 'object') return;
      switch (m.type) {
        case 'output':
          term.write(decodeBase64(m.data));
          break;
        case 'status':
          setBanner(m.text, m.level);
          break;
        case 'focus':
          term.focus();
          break;
      }
    });
  }

  window.addEventListener('focus', () => term.focus());
  post({ type: 'ready', cols: term.cols, rows: term.rows });
  term.focus();
})();
