/* Research wrapper. All terminal output stays bytes; never interpret it as HTML/JS. */
(() => {
  'use strict';
  let term, generation = 0, received = 0, blockedLinks = 0, externalPopupBlocked = false, topNavigationBlocked = false, researchMode = '';
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
    const lines = Array.from({length:b.length}, (_, i) => b.getLine(i).translateToString(true));
    return {generation, received, columns:term.cols, rows:term.rows,
      lines:b.length, maxLines:term.rows + 200, blockedLinks, externalPopupBlocked, topNavigationBlocked,
      utf8: lines.some(line => line.includes('café λ 世界 🐳')),
      cursorEdit: lines.some(line => line.includes('12XYZ')),
      recovered: lines.some(line => line.includes('RECOVERED')),
      ...(researchMode ? retentionStats() : {})};
  };
  // Read-only, version-specific research instrumentation; never a production API.
  // Report counts only, including stale cell-map entries and BOTH screen buffers.
  function retentionStats() {
    const core = term._core, parser = core._inputHandler._parser;
    let combinedUnits = 0, maxCombinedUnits = 0, extendedEntries = 0, markers = 0;
    for (const buffer of [core._bufferService.buffers.normal, core._bufferService.buffers.alt]) {
      markers += buffer.markers.length;
      for (let i = 0; i < buffer.lines.length; i++) {
        const line = buffer.lines.get(i);
        for (const value of Object.values(line._combined)) {
          combinedUnits += value.length; maxCombinedUnits = Math.max(maxCombinedUnits, value.length);
        }
        extendedEntries += Object.keys(line._extendedAttrs).length;
      }
    }
    const handlers = [...parser._oscParser._active, ...parser._dcsParser._active];
    const widths = core._renderService._renderer.value._widthCache._holey;
    return {combinedUnits, maxCombinedUnits, extendedEntries, markers,
      links:core._oscLinkService._dataByLinkId.size,
      normalLines:core._bufferService.buffers.normal.lines.length,
      alternateLines:core._bufferService.buffers.alt.lines.length,
      parserUnits:handlers.reduce((sum, handler) => sum + (handler._data?.length || 0), 0),
      parserLimited:handlers.some(handler => handler._hitLimit),
      params:parser._params.length, parserState:parser.currentState,
      a11yUnits:core._accessibilityManager.value?._liveRegion.textContent.length || 0,
      widthCacheEntries:widths.size,
      widthCacheKeyUnits:Array.from(widths.keys()).reduce((sum, key) => sum + key.length, 0),
      domNodes:document.getElementsByTagName('*').length};
  }
  window.surface = {
    start(id, probe = '') {
      generation = id; received = 0; blockedLinks = 0;
      researchMode = probe;
      term?.dispose(); document.getElementById('terminal').replaceChildren();
      term = new Terminal({cols:80, rows:24, scrollback:200, fontSize:14, logLevel:'off',
        allowProposedApi:false, screenReaderMode:true,
        linkHandler:{activate:()=>{ blockedLinks++; }, allowNonHttpProtocols:false}});
      term.open(document.getElementById('terminal'));
      // Experimental fidelity tradeoff: links were already inert. Consume their
      // metadata too, avoiding per-opening link/marker retention on an untrimmed row.
      // A fixed synthetic baseline probe alone can compare stock behavior.
      if (probe !== 'stock-links' && probe !== 'stock-alternate') term.parser.registerOscHandler(8, () => true);
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
        if (id === generation && target === term) post({kind:'ack', generation, sequence});
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
    if (method === 'stats' || method === 'resize') post({kind:'stats', generation,
      request:method === 'stats' ? (event.data.args[0] || 0) : 0,
      stats:method === 'stats' ? result : stats()});
  });
  // Prevent default link handling even if future markup gains an anchor.
  document.addEventListener('click', e => { if (e.target.closest?.('a')) e.preventDefault(); });
  document.addEventListener('contextmenu', e => e.preventDefault());
  document.addEventListener('securitypolicyviolation', event =>
    post({kind:'policy', generation, directive:event.effectiveDirective}));
  post({kind:'ready'});
})();
