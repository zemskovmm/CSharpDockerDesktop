/* Research wrapper. All terminal output stays bytes; never interpret it as HTML/JS. */
(() => {
  'use strict';
  let term, generation = 0, received = 0, blockedLinks = 0, externalPopupBlocked = false, topNavigationBlocked = false;
  const post = value => {
    const message = JSON.stringify(value);
    window.parent.postMessage(message, '*');
  };
  const encode = value => {
    const bytes = new TextEncoder().encode(value);
    if (bytes.length > 4096) { post({kind:'capacity', generation}); return; }
    post({kind:'input', generation, data:btoa(String.fromCharCode(...bytes))});
  };
  const stats = () => {
    const b = term.buffer.active;
    return {generation, received, columns:term.cols, rows:term.rows,
      lines:b.length, maxLines:term.rows + 200, blockedLinks, externalPopupBlocked, topNavigationBlocked,
      utf8: Array.from({length:b.length}, (_, i) => b.getLine(i).translateToString(true)).join('\n').includes('café λ 世界 🐳'),
      cursorEdit: Array.from({length:b.length}, (_, i) => b.getLine(i).translateToString(true)).join('\n').includes('12XYZ')};
  };
  window.surface = {
    start(id) {
      generation = id; received = 0; blockedLinks = 0;
      term?.dispose(); document.getElementById('terminal').replaceChildren();
      term = new Terminal({cols:80, rows:24, scrollback:200, fontSize:14, logLevel:'off',
        allowProposedApi:false, screenReaderMode:true,
        linkHandler:{activate:()=>{ blockedLinks++; }, allowNonHttpProtocols:false}});
      term.open(document.getElementById('terminal'));
      term.parser.registerOscHandler(52, () => true); // No remote clipboard writes/reads.
      term.onData(encode);
      term.onBinary(value => {
        if (value.length <= 4096) post({kind:'input', generation, data:btoa(value)});
      });
      term.focus();
    },
    write(id, sequence, base64) {
      if (id !== generation || !term) return;
      const target = term;
      const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
      received += bytes.length;
      target.write(bytes, () => {
        if (id === generation && target === term) post({kind:'ack', generation, sequence, stats:stats()});
      });
    },
    close(id) { generation = id; term?.dispose(); term = undefined;
      document.getElementById('terminal').replaceChildren(); },
    resize(columns, rows) { term?.resize(columns, rows); term?.focus(); },
    selection() { return term?.getSelection() || ''; },
    paste(text) { if (new TextEncoder().encode(text).length <= 4096) term?.paste(text); },
    selectFixture() { term?.select(0, 0, 7); },
    controlC() { encode('\x03'); },
    stats,
    // Fixed, non-payload probes; no host method accepts arbitrary JS or a URL.
    probePopup() { post({kind:'sandboxPopup', generation,
      blocked:window.open('https://example.invalid/terminal-probe') === null}); },
    probeNavigation() { location.href = 'https://example.invalid/terminal-probe'; },
    probeFetch() { fetch('https://example.invalid/terminal-probe').then(
      ()=>post({kind:'fetch', generation, blocked:false}),
      ()=>post({kind:'fetch', generation, blocked:true})); },
    probeExternal() {
      externalPopupBlocked = window.open('mailto:terminal-probe@example.invalid') === null;
      try { window.parent.location.href = 'mailto:terminal-probe@example.invalid'; }
      catch { topNavigationBlocked = true; }
      location.href = 'mailto:terminal-probe@example.invalid';
    }
  };
  const allowed = new Set(['start','write','close','resize','selection','paste','selectFixture',
    'controlC','stats','probePopup','probeNavigation','probeFetch','probeExternal']);
  window.addEventListener('message', event => {
    if (event.source !== window.parent || !allowed.has(event.data?.method)) return;
    const method = event.data.method;
    const result = window.surface[method](...event.data.args);
    if (method === 'selection') post({kind:'copy', generation, data:result});
    if (method === 'stats' || method === 'resize') post({kind:'stats', generation, stats:stats()});
  });
  // Prevent default link handling even if future markup gains an anchor.
  document.addEventListener('click', e => { if (e.target.closest?.('a')) e.preventDefault(); });
  document.addEventListener('contextmenu', e => e.preventDefault());
  document.addEventListener('securitypolicyviolation', event =>
    post({kind:'policy', generation, directive:event.effectiveDirective}));
  post({kind:'ready'});
})();
