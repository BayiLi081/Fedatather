(function () {
  if (window.top !== window || window.__fedatatherSelection) return;
  window.__fedatatherSelection = true;
  var CSS = '.bar{display:flex;align-items:center;gap:2px;padding:3px;border-radius:999px;background:#fff;color:#1b1b1d;' +
    'box-shadow:0 4px 18px rgba(0,0,0,.18),0 0 0 1px rgba(0,0,0,.08);font:13px/1 Segoe UI,system-ui,sans-serif;' +
    'white-space:nowrap;user-select:none;-webkit-user-select:none}' +
    'button{all:unset;box-sizing:border-box;cursor:pointer;padding:7px 11px;border-radius:999px;color:inherit;font:inherit}' +
    'button:hover{background:rgba(0,0,0,.07)}button:active{background:rgba(0,0,0,.12)}' +
    'button:focus-visible{outline:2px solid #0067c0;outline-offset:-2px}' +
    '.back{padding:7px 9px}.label{padding:0 6px 0 4px;opacity:.6}' +
    '.sep{width:1px;align-self:stretch;margin:5px 2px;background:rgba(0,0,0,.12)}' +
    '@media (prefers-color-scheme:dark){.bar{background:#2b2b2e;color:#f2f2f4;' +
    'box-shadow:0 4px 18px rgba(0,0,0,.5),0 0 0 1px rgba(255,255,255,.1)}' +
    'button:hover{background:rgba(255,255,255,.1)}button:active{background:rgba(255,255,255,.16)}' +
    'button:focus-visible{outline-color:#4cc2ff}.sep{background:rgba(255,255,255,.15)}}';
  var host = null, bar = null, text = '', range = null, point = null, field = null;

  function shown() { return !!(host && host.parentNode); }
  function inBar(e) { return !!host && e.composedPath && e.composedPath().indexOf(host) >= 0; }

  function build() {
    host = document.createElement('fedatather-selection');
    host.style.cssText = 'all:initial;position:fixed;left:0;top:0;z-index:2147483647;';
    var root = host.attachShadow({ mode: 'closed' });
    try {
      var sheet = new CSSStyleSheet();
      sheet.replaceSync(CSS);
      root.adoptedStyleSheets = [sheet];
    } catch (e) {
      var style = document.createElement('style');
      style.textContent = CSS;
      root.appendChild(style);
    }
    bar = document.createElement('div');
    bar.className = 'bar';
    root.appendChild(bar);
    // Clicking the bar must not clear the selection.
    root.addEventListener('mousedown', function (e) { e.preventDefault(); e.stopPropagation(); });
  }

  function add(el) { bar.appendChild(el); return el; }
  function button(label, title, onClick, cls) {
    var b = document.createElement('button');
    b.textContent = label;
    b.title = title;
    if (cls) b.className = cls;
    b.addEventListener('click', function (e) { e.preventDefault(); e.stopPropagation(); onClick(); });
    return add(b);
  }
  function sep() { var s = document.createElement('span'); s.className = 'sep'; add(s); }

  function showMain() {
    bar.textContent = '';
    button('译 Translate', 'Translate into Chinese with Google Translate', function () { go('translate'); });
    sep();
    button('Ask AI ▾', 'Ask an AI about the selected text', showAi);
    place();
  }

  function showAi() {
    bar.textContent = '';
    button('‹', 'Back', showMain, 'back');
    var l = document.createElement('span'); l.className = 'label'; l.textContent = 'Ask'; add(l);
    button('ChatGPT', 'Ask ChatGPT', function () { go('chatgpt'); });
    button('Claude', 'Ask Claude', function () { go('claude'); });
    button('Gemini', 'Ask Gemini (the text is also copied, to paste if it is not filled in)', function () { go('gemini'); });
    place();
  }

  function go(action) {
    try { window.chrome.webview.postMessage({ selAction: action, text: text }); } catch (e) { }
    hide();
  }

  function hide() {
    if (shown()) host.parentNode.removeChild(host);
    range = point = field = null;
  }

  // Above the selection, centred on it; below it when there's no room above. Kept inside the window.
  function place() {
    if (!shown()) return;
    var r = null;
    if (range) {
      r = range.getBoundingClientRect();
      if (!r.width && !r.height) r = null;
    }
    if (!r && point) r = { left: point.x, right: point.x, top: point.y, bottom: point.y, width: 0 };
    if (!r && field) r = field.getBoundingClientRect();
    if (!r) { hide(); return; }
    var w = bar.offsetWidth, h = bar.offsetHeight, vw = document.documentElement.clientWidth || innerWidth, vh = innerHeight;
    var x = Math.max(8, Math.min(r.left + r.width / 2 - w / 2, vw - w - 8));
    var y = r.top - h - 8;
    if (y < 8) y = r.bottom + 8;
    host.style.visibility = r.bottom < 0 || r.top > vh ? 'hidden' : 'visible';
    host.style.transform = 'translate(' + Math.round(x) + 'px,' + Math.round(y) + 'px)';
  }

  // The selected text and where it is: in a text box (its own selection) or in the page.
  function current() {
    var a = document.activeElement;
    if (a && (a.tagName === 'TEXTAREA' || (a.tagName === 'INPUT' && /^(text|search|url|email|tel)$/i.test(a.type)))) {
      try {
        if (a.selectionEnd > a.selectionStart)
          return { text: a.value.slice(a.selectionStart, a.selectionEnd), range: null, field: a };
      } catch (e) { }
      return null;
    }
    if (a && a.tagName === 'INPUT') return null; // passwords and other boxes: never offered
    var s = window.getSelection();
    if (!s || s.isCollapsed || !s.rangeCount) return null;
    return { text: s.toString(), range: s.getRangeAt(0), field: null };
  }

  function check(e) {
    // After the click that ends the selection has finished changing it.
    setTimeout(function () {
      var c = current();
      if (!c || !c.text.trim()) { hide(); return; }
      text = c.text.trim();
      range = c.range;
      field = c.field;
      point = e ? { x: e.clientX, y: e.clientY } : null;
      if (!host) build();
      if (!shown()) document.documentElement.appendChild(host);
      showMain();
    }, 0);
  }

  addEventListener('mousedown', function (e) { if (!inBar(e)) hide(); }, true);
  addEventListener('mouseup', function (e) { if (e.button === 0 && !inBar(e)) check(e); }, true);
  addEventListener('keyup', function (e) {
    if (e.key === 'Shift' || e.shiftKey || ((e.ctrlKey || e.metaKey) && (e.key === 'a' || e.key === 'A'))) check(null);
  }, true);
  addEventListener('keydown', function (e) { if (e.key === 'Escape' && shown()) hide(); }, true);
  addEventListener('scroll', place, true);
  addEventListener('resize', place);
  document.addEventListener('selectionchange', function () { if (shown() && !current()) hide(); });
})();