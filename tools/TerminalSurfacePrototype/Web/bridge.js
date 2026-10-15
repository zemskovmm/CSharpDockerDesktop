// Trusted outer document only. The terminal has an opaque sandbox origin and
// cannot access this document, its WebView bridge, top navigation or popups.
(() => {
  'use strict';
  const frame = document.getElementById('terminal-frame');
  let cachedStats = null;
  const violations = {};
  const note = directive => {
    if (['connect-src','frame-src','script-src','script-src-elem','default-src'].includes(directive))
      violations[directive] = Math.min((violations[directive] || 0) + 1, 1000000);
  };
  document.addEventListener('securitypolicyviolation', event => note(event.effectiveDirective));
  const nativePost = message => {
    if (window.chrome?.webview) window.chrome.webview.postMessage(message);
    else if (window.webkit?.messageHandlers?.unoWebView)
      window.webkit.messageHandlers.unoWebView.postMessage(message);
  };
  window.addEventListener('message', event => {
    if (event.source !== frame.contentWindow || typeof event.data !== 'string'
      || event.data.length > 12000) return;
    let value;
    try { value = JSON.parse(event.data); } catch { return; }
    if (value.kind === 'policy') note(value.directive);
    if (value.stats) cachedStats = value.stats;
    if (['ready','ack','input','capacity','copy','fetch','sandboxPopup'].includes(value.kind))
      nativePost(event.data);
  });
  window.surface = { stats:() => ({...cachedStats, violations}), requestStats:() => send('stats', []) };
  function send(method, args) { frame.contentWindow.postMessage({method, args}, '*'); }
  for (const method of ['start','write','close','resize','selection','paste','selectFixture',
    'controlC','probePopup','probeNavigation','probeFetch','probeExternal'])
    window.surface[method] = (...args) => send(method, args);
})();
