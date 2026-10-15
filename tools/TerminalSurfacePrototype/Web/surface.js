/* Research wrapper. All terminal output stays bytes; never interpret it as HTML/JS. */
(() => {
  'use strict';
  let term, generation = 0, received = 0, blockedLinks = 0, externalPopupBlocked = false, topNavigationBlocked = false, researchMode = '', failed = false, lastSequence = 0, a11yNavigated = false, a11yWriteHeld = false;
  // Only weak references and numeric snapshots: diagnostics must not root old terminals.
  const retired = [];
  let disposed = 0, finalized = 0, lastDisposed = {};
  let starts = 0, closes = 0, constructed = 0, opened = 0, renders = 0;
  let acceptedWrites = 0, staleWrites = 0, inactiveWrites = 0;
  const finalizer = typeof FinalizationRegistry === 'function'
    ? new FinalizationRegistry(() => finalized++) : null;
  const lifecycleStats = () => ({terminalPresent:!!term, disposed, finalized,
    starts, closes, constructed, opened, renders, acceptedWrites, staleWrites, inactiveWrites,
    emptyControl:researchMode === 'aggregate-empty-cycles',
    weakRefSupported:typeof WeakRef === 'function',
    retiredRootsAlive:retired.filter(ref => ref.deref() !== undefined).length,
    attachedTerminalNodes:document.querySelectorAll('.terminal').length,
    domNodes:document.getElementsByTagName('*').length, ...lastDisposed});
  const disposeTerminal = target => {
    const core = target._core, renderer = core._renderService._renderer.value;
    if (researchMode.startsWith('aggregate-')) {
      disposed++;
      if (typeof WeakRef === 'function') {
        if (retired.length < 256) retired.push(new WeakRef(target), new WeakRef(core),
          new WeakRef(core._bufferService.buffers), new WeakRef(renderer));
        finalizer?.register(target, disposed);
      }
    }
    target.dispose();
    if (researchMode.startsWith('aggregate-')) lastDisposed = {
      disposedWidthEntries:renderer._widthCache._holey?.size || 0,
      disposedWidthUnits:renderer._widthCache._keyUnits,
      disposedWriteChunks:core._writeBuffer._writeBuffer.length,
      disposedWriteCallbacks:core._writeBuffer._callbacks.length,
      disposedRenderFramePending:!!core._renderService._renderDebouncer._animationFrame};
  };
  const post = value => {
    const message = JSON.stringify(value);
    window.parent.postMessage(message, '*');
  };
  const encode = value => {
    if (failed || !term || researchMode === 'a11y-no-echo') return;
    const bytes = new TextEncoder().encode(value);
    if (bytes.length > 4096) { post({kind:'capacity', generation}); return; }
    post({kind:'input', generation, data:btoa(String.fromCharCode(...bytes))});
  };
  const failSurface = (target, id, sequence, code) => {
    if (id !== generation || target !== term || failed) return;
    const bounds = researchMode ? retentionStats() : undefined;
    failed = true; term = undefined;
    // Fence synchronously, dispose after the parser/key/render stack unwinds.
    queueMicrotask(() => { disposeTerminal(target);
      if (id === generation) document.getElementById('terminal').replaceChildren(); });
    post({kind:'failure', generation:id, sequence, code, received, bounds});
  };
  const stats = () => {
    if (!term) return {generation, received, ...lifecycleStats()};
    const b = term.buffer.active;
    const lines = Array.from({length:b.length}, (_, i) => b.getLine(i).translateToString(true));
    return {generation, received, columns:term.cols, rows:term.rows,
      lines:b.length, maxLines:term.rows + 200, blockedLinks, externalPopupBlocked, topNavigationBlocked,
      utf8: lines.some(line => line.includes('café λ 世界 🐳')),
      cursorEdit: lines.some(line => line.includes('12XYZ')),
      recovered: lines.some(line => line.includes('RECOVERED')),
      a11yNavigated, a11yWriteHeld, ...lifecycleStats(),
      combinedExact: lines.some(line => line.includes('a' + '\u0301'.repeat(255))),
      combinedShort: lines.some(line => line.includes('b\u0301')),
      combinedReflow: lines.join('').includes('a\u0301Y'),
      ...(researchMode ? retentionStats() : {})};
  };
  // Read-only, version-specific research instrumentation; never a production API.
  // Report counts only, including stale cell-map entries and BOTH screen buffers.
  function retentionStats() {
    const core = term._core, parser = core._inputHandler._parser;
    let combinedUnits = 0, maxCombinedUnits = 0, extendedEntries = 0, markers = 0;
    let combinedEntries = 0, staleCombinedEntries = 0, staleExtendedEntries = 0;
    let cellArrayBytes = 0, cellBackingBytes = 0, maxLineColumns = 0;
    const backings = new Set(), retainedLines = new Set(), logicalLines = new Set();
    let backingSlots = 0, cleanupTasks = 0, cleanupCallbacks = 0;
    for (const buffer of [core._bufferService.buffers.normal, core._bufferService.buffers.alt]) {
      for (let i=0; i<buffer.lines.length; i++) logicalLines.add(buffer.lines.get(i));
      backingSlots += buffer.lines._array.length;
      for (const line of buffer.lines._array) if (line) retainedLines.add(line);
      cleanupTasks += buffer._memoryCleanupQueue._tasks.length;
      cleanupCallbacks += Number(!!buffer._memoryCleanupQueue._idleCallback);
    }
    if (core._bufferService._cachedBlankLine) retainedLines.add(core._bufferService._cachedBlankLine);
    for (const buffer of [core._bufferService.buffers.normal, core._bufferService.buffers.alt]) {
      markers += buffer.markers.length;
    }
    for (const line of retainedLines) {
        cellArrayBytes += line._data.byteLength;
        maxLineColumns = Math.max(maxLineColumns, line.length);
        if (!backings.has(line._data.buffer)) { cellBackingBytes += line._data.buffer.byteLength; backings.add(line._data.buffer); }
        for (const [key, value] of Object.entries(line._combined)) {
          combinedEntries++;
          if (!(line._data[Number(key) * 3] & 0x200000)) staleCombinedEntries++;
          combinedUnits += value.length; maxCombinedUnits = Math.max(maxCombinedUnits, value.length);
        }
        for (const key of Object.keys(line._extendedAttrs)) {
          extendedEntries++;
          if (!(line._data[Number(key) * 3 + 2] & 0x10000000)) staleExtendedEntries++;
        }
    }
    const a11y = core._accessibilityManager.value;
    const handlers = [...parser._oscParser._active, ...parser._dcsParser._active];
    const cache = core._renderService._renderer.value._widthCache, widths = cache._holey;
    let widthRenderedCells = 0, widthRenderedWidth = 0;
    const row = term.buffer.active.getLine(term.buffer.active.baseY + term.buffer.active.cursorY);
    for (let i=0; row && i<term.cols; i++) {
      const cell = row.getCell(i);
      const value = cell.getChars();
      const variant = (cell.isBold() ? 1 : 0) | (cell.isItalic() ? 2 : 0);
      if (value.length > 1 && widths.has(String(variant) + value)) {
        widthRenderedCells++; widthRenderedWidth += widths.get(String(variant) + value);
      }
    }
    const handler = core._inputHandler, writer = core._writeBuffer;
    const renderer = core._renderService._renderer.value, debouncer = core._renderService._renderDebouncer;
    return {combinedUnits, maxCombinedUnits, extendedEntries, markers,
      combinedEntries, staleCombinedEntries, staleExtendedEntries, cellArrayBytes, cellBackingBytes, maxLineColumns,
      retainedLineObjects:retainedLines.size, nonLogicalLineObjects:[...retainedLines].filter(line => !logicalLines.has(line)).length,
      backingSlots, cleanupTasks, cleanupCallbacks,
      titleUnits:handler._windowTitle.length + handler._iconName.length,
      titleStackEntries:handler._windowTitleStack.length + handler._iconNameStack.length,
      titleStackUnits:[...handler._windowTitleStack,...handler._iconNameStack].reduce((n,s) => n+s.length,0),
      parseBufferBytes:handler._parseBuffer.byteLength, parserPaused:handler._parseStack.paused,
      writeChunks:writer._writeBuffer.length, writeCallbacks:writer._callbacks.filter(Boolean).length,
      writePendingUnits:writer._pendingData,
      renderFramePending:!!debouncer._animationFrame, renderCallbacks:debouncer._refreshCallbacks.length,
      rendererRows:renderer._rowElements.length,
      rendererNodes:renderer._rowContainer.querySelectorAll('*').length,
      a11yRowUnits:a11y?._rowElements.reduce((n,row) => n+row.textContent.length,0) || 0,
      normalCapacity:core._bufferService.buffers.normal.lines.maxLength,
      alternateCapacity:core._bufferService.buffers.alt.lines.maxLength,
      links:core._oscLinkService._dataByLinkId.size,
      normalLines:core._bufferService.buffers.normal.lines.length,
      alternateLines:core._bufferService.buffers.alt.lines.length,
      parserUnits:handlers.reduce((sum, handler) => sum + (handler._data?.length || 0), 0),
      parserLimited:handlers.some(handler => handler._hitLimit),
      params:parser._params.length, parserState:parser.currentState,
      a11yUnits:a11y?._liveRegion.textContent.length || 0,
      a11yPendingUnits:a11y?._charsToAnnounce.length || 0,
      a11yAggregateUnits:(a11y?._liveRegion.textContent.length || 0) + (a11y?._charsToAnnounce.length || 0),
      a11yEchoEntries:a11y?._charsToConsume.length || 0,
      a11yEchoUnits:a11y?._researchEchoUnits || 0,
      a11yMaxEchoEntryUnits:Math.max(0, ...(a11y?._charsToConsume || []).map(value => value.length)),
      a11yRows:a11y?._rowElements.length || 0,
      a11yRefreshPending:!!a11y?._liveRegionDebouncer._refreshTimeoutID,
      widthCacheEntries:widths.size,
      widthCacheAccountedUnits:cache._keyUnits,
      widthCacheEvictions:cache._researchEvictions,
      widthCacheMeasurements:cache._researchMeasurements,
      widthCacheHits:cache._researchHits,
      widthCachePeakEntries:cache._researchPeakEntries,
      widthCachePeakKeyUnits:cache._researchPeakKeyUnits,
      widthMeasureUnits:cache._measureElements.reduce((n, el) => n + el.textContent.length, 0),
      widthRenderedCells, widthRenderedWidth,
      widthCacheKeyUnits:Array.from(widths.keys()).reduce((sum, key) => sum + key.length, 0),
      domNodes:document.getElementsByTagName('*').length};
  }
  window.surface = {
    start(id, probe = '') {
      generation = id; failed = false; received = 0; lastSequence = 0; a11yNavigated = false; a11yWriteHeld = false; blockedLinks = 0;
      // Dispose under the previous mode before switching, including terminal -> empty.
      if (term) disposeTerminal(term); term = undefined;
      document.getElementById('terminal').replaceChildren();
      researchMode = probe; starts++;
      // Same opaque document and bridge lifecycle; no xterm construction/open/render.
      if (probe === 'aggregate-empty-cycles') return;
      term = new Terminal({cols:80, rows:24, scrollback:200, fontSize:14, logLevel:'off',
        allowProposedApi:false, screenReaderMode:true,
        linkHandler:{activate:()=>{ blockedLinks++; }, allowNonHttpProtocols:false}});
      constructed++;
      const target = term;
      term.onRender(() => renders++);
      target._core.onResearchAccessibilityFailure(() => failSurface(target, id, lastSequence, 'accessibility-limit'));
      term.open(document.getElementById('terminal')); opened++;
      // Experimental fidelity tradeoff: links were already inert. Consume their
      // metadata too, avoiding per-opening link/marker retention on an untrimmed row.
      // A fixed synthetic baseline probe alone can compare stock behavior.
      if (probe !== 'stock-links' && probe !== 'stock-alternate') term.parser.registerOscHandler(8, () => true);
      term.parser.registerOscHandler(52, () => true); // No remote clipboard writes/reads.
      term.onData(encode);
      term.onBinary(value => {
        if (!failed && term && value.length <= 4096) post({kind:'input', generation, data:btoa(value)});
      });
      term.focus();
    },
    write(id, sequence, base64) {
      if (id !== generation) { staleWrites++; return; }
      if (!term || failed) { inactiveWrites++; return; }
      acceptedWrites++;
      const target = term; lastSequence = sequence;
      const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
      received += bytes.length;
      // The checksum-pinned source build extends ONLY this callback with a fixed
      // experimental failure code. This is not a stock xterm API or production policy.
      target.write(bytes, failure => {
        if (id !== generation || target !== term || failed) return;
        if (failure === 'combined-cell-limit' || failure === 'accessibility-limit') {
          failSurface(target, id, sequence, failure); return;
        }
        post({kind:'ack', generation, sequence});
      });
    },
    close(id) { closes++; failed = true; generation = id; if (term) disposeTerminal(term); term = undefined;
      document.getElementById('terminal').replaceChildren(); },
    resize(columns, rows) { term?.resize(columns, rows); term?.focus(); },
    selection() { return term?.getSelection() || ''; },
    paste(text) { if (new TextEncoder().encode(text).length <= 4096) term?.paste(text); },
    selectFixture() { term?.select(0, 0, 7); },
    controlC() { encode('\x03'); },
    stats,
    probeWidthCache(action) {
      if (!researchMode.startsWith('width-cache') || !term || failed) return;
      if (action === 'refresh') term.refresh(0, term.rows - 1);
      if (action === 'font') term.options.fontSize = term.options.fontSize === 14 ? 15 : 14;
    },
    probeAccessibility(action) {
      if (!researchMode || !term || failed) return;
      const a = term._core._accessibilityManager.value;
      if (action === 'keys-exact' || action === 'keys-over') {
        // Real xterm keyboard handlers; controlled no-echo means no onData forwarding.
        const count = action === 'keys-exact' ? 256 : 1;
        for (let i=0; i<count && !failed; i++)
          term.textarea.dispatchEvent(new KeyboardEvent('keydown', {key:'x', keyCode:88, bubbles:true}));
      } else if (action === 'hold-write') {
        term.parser.registerCsiHandler({final:'z'}, () => new Promise(resolve => {
          a11yWriteHeld = true;
          setTimeout(() => resolve(true), 1500);
        }));
      } else if (action === 'queue-refresh') {
        a._refreshRows(); a._refreshRows();
      } else if (action === 'blur') term.blur();
      else if (action === 'focus') term.focus();
      else if (action === 'delayed-failure') {
        // Fault injection into the actual debouncer callback: next prospective
        // character at an exact aggregate boundary must fail without escaping.
        a._renderRows = () => a._handleChar('X');
        a._liveRegionDebouncer.refresh(0, 0, term.rows);
      } else if (action === 'navigation') {
        const before = term._core.buffer.ydisp;
        a._rowElements[1].focus(); a._rowElements[0].focus();
        a11yNavigated = before > 0 && term._core.buffer.ydisp === before - 1
          && document.activeElement === a._rowElements[1];
        term.focus();
      }
    },
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
    'controlC','stats','probeWidthCache','probeAccessibility','probePopup','probeNavigation','probeFetch','probeExternal']);
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
