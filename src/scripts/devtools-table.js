(function () {
  function range(a, b) { return '[' + String.fromCharCode(a) + '-' + String.fromCharCode(b) + ']'; }
  // icon-font glyphs (private use characters) are decoration, not data
  var PUA = new RegExp(range(0xE000, 0xF8FF), 'g');
  // half an emoji (a lone surrogate) would stop ExecuteScript from ever returning
  var LONE = new RegExp(range(0xD800, 0xDBFF) + '(?!' + range(0xDC00, 0xDFFF) + ')|(?<!' + range(0xD800, 0xDBFF) + ')' + range(0xDC00, 0xDFFF), 'g');
  function clean(s) { return (s || '').replace(PUA, '').replace(LONE, String.fromCharCode(0xFFFD)).replace(/\s+/g, ' ').trim(); }
  var out = [];
  function add(caption, rows, kind, sig, key) {
    var cols = 0, filled = 0, used = 0;
    rows.forEach(function (r) { cols = Math.max(cols, r.length); r.forEach(function (c) { if (c) filled++; }); });
    for (var c = 0; c < cols; c++) if (rows.some(function (r) { return r[c]; })) used++;
    if (rows.length < 2 || used < 2 || filled < 4) return false;
    out.push({ caption: caption, rows: rows, kind: kind || 'table', sig: sig || '', key: key || '' });
    return true;
  }
  var tables = document.querySelectorAll('table');
  for (var i = 0; i < tables.length && out.length < 100; i++) {
    var t = tables[i];
    if (t.querySelector('table')) continue;
    var rows = [];
    for (var r = 0; r < t.rows.length && r < 5000; r++) {
      var row = [], cells = t.rows[r].cells;
      for (var c = 0; c < cells.length; c++) {
        row.push(clean(cells[c].innerText));
        for (var k = 1; k < Math.min(cells[c].colSpan || 1, 50); k++) row.push('');
      }
      rows.push(row);
    }
    add(t.caption ? clean(t.caption.innerText) : clean(t.getAttribute('aria-label') || t.getAttribute('title')), rows);
  }
  var grids = document.querySelectorAll('[role=table], [role=grid], [role=treegrid]');
  for (var g = 0; g < grids.length && out.length < 100; g++) {
    var el = grids[g];
    if (el.tagName === 'TABLE') continue;
    var rws = el.querySelectorAll('[role=row]'), data = [];
    for (var j = 0; j < rws.length && j < 5000; j++) {
      var cs = rws[j].querySelectorAll('[role=cell], [role=gridcell], [role=columnheader], [role=rowheader]'), rr = [];
      for (var q = 0; q < cs.length; q++) rr.push(clean(cs[q].innerText));
      data.push(rr);
    }
    add(clean(el.getAttribute('aria-label')), data);
  }

  // ---- 3. repeated lists
  var SKIP = /^(script|style|svg|template|noscript|select|option|head|table|thead|tbody|tfoot|tr)$/i;
  // already covered by tables, or site chrome (menus, headers, footers) rather than content
  var CHROME = 'table, [role=table], [role=grid], [role=treegrid], nav, header, footer, aside, [role=navigation], ' +
               '[role=banner], [role=contentinfo], [role=menu], [role=menubar], [role=tablist]';
  // Columns are named after readable class names (title, author-name) or test ids. Names made by CSS-in-JS, CSS
  // modules or minifiers (css-1x2y3z, jftiEf, d4r55) and pure styling classes (fontBodyMedium) don't count.
  var HASHY = /^(css|sc|jsx|svelte|emotion)-|[_-](?=[A-Za-z]*\d)[A-Za-z0-9]{5,}$|^(?=[a-z]*\d)[a-z0-9]{6,}$|___[A-Za-z0-9_-]{4,}$|[a-z]\d+[a-z]/i;
  var STYLING = /^(font|text|bg|flex|grid|align|justify|gap|border|rounded|shadow)[-_A-Z]|(^|[-_])(symbols?|icons?)$|[a-z](Symbols?|Icons?)$/;
  function readable(c) {
    if (!c || c.length < 3 || c.length > 40 || HASHY.test(c) || STYLING.test(c)) return false;
    var words = c.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2').split(/[\s_\d-]+/);
    for (var i = 0; i < words.length; i++) {
      var w = words[i];
      if (!w || /^(id|no|ui|img|btn|msg|txt|src|cnt|qty)$/i.test(w)) continue;
      // RfnDt, rsqaWe, hfpxzc, gllhef, Ppzolf: too short, no vowels, unpronounceable
      if (w.length < 3 || !/[aeiouy]/i.test(w) || /[b-df-hj-np-tv-xz]{4}/i.test(w) || /^(.)\1/i.test(w)) return false;
    }
    return true;
  }
  var NAMING = ['itemprop', 'data-testid', 'data-test-id', 'data-test', 'data-qa', 'data-e2e', 'data-field'];
  function named(el) {
    for (var i = 0; i < NAMING.length; i++) if (readable(el.getAttribute(NAMING[i]))) return el.getAttribute(NAMING[i]);
    for (var j = 0; j < el.classList.length; j++) if (readable(el.classList[j])) return el.classList[j];
    return '';
  }
  function pretty(s) { return clean((s || '').replace(/[-_]+/g, ' ')); }
  function shown(el) { return el.getClientRects().length > 0; }
  function abs(u) { try { return new URL(u, document.baseURI).href; } catch (e) { return u; } }
  function bgImage(el) {
    var m = el.style && /url\(\s*['"]?(.*?)['"]?\s*\)/.exec(el.style.backgroundImage || '');
    return m && m[1] && !/^data:/i.test(m[1]) ? abs(m[1]) : '';
  }
  function classes(el, n) {
    var s = '';
    for (var i = 0; i < el.classList.length && i < n; i++) s += '.' + el.classList[i];
    return s;
  }
  function sample(arr, k) {
    if (arr.length <= k) return arr;
    var picked = [], step = arr.length / k;
    for (var i = 0; i < k; i++) picked.push(arr[Math.floor(i * step)]);
    return picked;
  }

  var cands = [], all = document.body ? document.body.getElementsByTagName('*') : [];
  for (var a = 0; a < all.length; a++) {
    var p = all[a], kids = p.children;
    if (kids.length < 3 || SKIP.test(p.tagName) || p.closest(CHROME)) continue;
    // Group the children by tag + first class. Every group of 3+ look-alikes may be a list, also when the items
    // are interleaved with spacers and dividers (Google Maps reviews).
    var groups = {}, names = [];
    for (var b = 0; b < kids.length; b++) {
      var ch = kids[b];
      if (SKIP.test(ch.tagName)) continue;
      var sg = ch.tagName + '.' + (ch.classList[0] || '');
      if (!groups[sg]) { groups[sg] = []; names.push(sg); }
      groups[sg].push(ch);
    }
    for (var gi = 0; gi < names.length; gi++) {
      if (groups[names[gi]].length < 3) continue;
      var items = groups[names[gi]].filter(shown);
      if (items.length < 3 || /^(P|BR|HR|H[1-6]|BLOCKQUOTE)$/.test(items[0].tagName)) continue;   // runs of prose
      var some = sample(items, 200), text = 0, links = 0, imgs = 0;
      for (var d = 0; d < some.length; d++) {
        var it = some[d], len = (it.textContent || '').trim().length, ctl = 0;
        // text on buttons is mostly controls (Sort, Like, Share), not content
        var bs = it.querySelectorAll('button, [role=button]');
        for (var q = 0; q < bs.length && q < 40; q++) ctl += (bs[q].textContent || '').trim().length;
        text += Math.min(Math.max(len - ctl * 0.75, 0), 300);
        if (it.tagName === 'A' || it.querySelector('a[href], [data-href]')) links++;
        if (it.tagName === 'IMG' || it.querySelector('img, [style*=background-image]')) imgs++;
      }
      var m = some.length, avg = text / m;
      if (avg < 4 && imgs < m / 2) continue;
      cands.push({ el: p, items: items, score: items.length * (Math.min(avg, 120) / 4 + 10 * links / m + 10 * imgs / m) });
    }
  }

  var RATING = /\bstars?\b|rating|rated|评分|星/i;
  var DATE = /\b(ago|yesterday|today|just now)\b|^\d{1,4}[-\/.]\d{1,2}([-\/.]\d{1,4})?\b|\b(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+\d|\d+\s*(秒|分钟|小时|天|周|个月|年)前|昨天|今天|刚刚|\d+月\d+日/i;
  var NUMBER = /^[+\-]?\d[\d.,]*\s*[%kmw万千亿+]?$/i;
  var INLINE = /^(BR|WBR|B|I|EM|STRONG|U|S|SMALL|SUB|SUP|MARK|CODE)$/;
  // an id attribute on every item (data-review-id, data-id...) recognises the item again on later scans
  function idAttr(items) {
    var at = items[0].attributes;
    for (var i = 0; i < at.length; i++) {
      var nm = at[i].name, seen = {}, distinct = 0;
      if (!/^data-([a-z]+[-_])*id$/i.test(nm)) continue;
      for (var j = 0; j < items.length; j++) {
        var v = items[j].getAttribute(nm);
        if (v && !seen[v]) { seen[v] = 1; distinct++; }
      }
      if (distinct >= items.length * 0.9) return nm;
    }
    return '';
  }

  function listRows(items) {
    var order = [], cols = {}, data = [];
    items.forEach(function (item) {
      var rec = {};
      function put(key, v, label, type) {
        v = clean(v);
        if (!v || v.length > 20000 || rec[key] !== undefined) return;
        rec[key] = v;
        if (!cols[key]) { cols[key] = { n: 0, label: label || '', type: type }; order.push(key); }
        cols[key].n++;
      }
      function visit(el, key, label) {
        var t = el.tagName, aria = el.getAttribute('aria-label');
        if (t === 'IMG') { put(key + '@img', el.currentSrc || el.src, label, 'img'); return; }
        // star ratings are often just an image with a label: <span role=img aria-label='4 stars'>
        if (el.getAttribute('role') === 'img' && aria) { put(key + '@label', aria, RATING.test(aria) ? 'rating' : label, 'text'); return; }
        var href = t === 'A' ? el.href : el.getAttribute('data-href');
        if (href && !/^javascript:/i.test(href)) put(key + '@link', abs(href), label, 'link');
        var bg = bgImage(el);
        if (bg) put(key + '@bg', bg, label, 'img');
        var inline = true;
        for (var k = 0; k < el.children.length && inline; k++) inline = INLINE.test(el.children[k].tagName);
        // plain or lightly formatted text (line breaks, bold) is one value
        if (inline) { put(key, el.innerText || el.textContent, t === 'TIME' ? 'date' : label, 'text'); return; }
        var own = '';
        for (var i = 0; i < el.childNodes.length; i++) if (el.childNodes[i].nodeType === 3) own += ' ' + el.childNodes[i].nodeValue;
        put(key + '@text', own, label, 'text');
        var seen = {};
        for (var j = 0; j < el.children.length; j++) {
          var c = el.children[j];
          if (SKIP.test(c.tagName)) continue;
          var seg = c.tagName + '.' + (c.classList[0] || '');
          seen[seg] = (seen[seg] || 0) + 1;
          visit(c, key + '>' + seg + '#' + seen[seg], named(c) || label);
        }
      }
      visit(item, '', named(item));
      data.push(rec);
    });

    var n = data.length, min = Math.max(2, Math.ceil(n * 0.25)), kept = [], same = {};
    order.forEach(function (k) {
      if (cols[k].n < min) return;
      var vals = data.map(function (r) { return r[k] || ''; }), distinct = {}, count = 0;
      vals.forEach(function (v) { if (v && !distinct[v]) { distinct[v] = 1; count++; } });
      if (count < 2) return;                       // one fixed value wherever it appears: icons, button labels
      var whole = vals.join('|~|');
      if (same[whole]) return;                     // duplicate of an earlier column (same link twice)
      same[whole] = 1;
      kept.push(k);
    });
    if (kept.length < 2) return null;
    // how alike the items are: the share of each item's fields that most of the items have
    var alike = 0;
    data.forEach(function (r) {
      var ks = Object.keys(r), common = 0;
      ks.forEach(function (k) { if (cols[k].n >= n / 2) common++; });
      alike += ks.length ? common / ks.length : 0;
    });
    // readable text first (name, rating, date, text...), then links, then images
    function rank(k) { return cols[k].type === 'link' ? 1 : cols[k].type === 'img' ? 2 : 0; }
    kept = kept.map(function (k, i) { return { k: k, i: i }; })
      .sort(function (x, y) { return rank(x.k) - rank(y.k) || x.i - y.i; })
      .map(function (o) { return o.k; })
      .slice(0, 20);
    // a column without a usable class name is named after what it holds
    var gotName = false;
    function guess(k) {
      var vals = [], dates = 0, nums = 0, len = 0, seen = {}, distinct = 0;
      data.forEach(function (r) { if (r[k]) vals.push(r[k]); });
      vals.forEach(function (v) {
        if (DATE.test(v)) dates++;
        if (NUMBER.test(v)) nums++;
        len += v.length;
        if (!seen[v]) { seen[v] = 1; distinct++; }
      });
      if (dates >= vals.length * 0.6) return 'date';
      if (nums >= vals.length * 0.8) return 'number';
      if (len >= vals.length * 40) return 'text';
      if (!gotName && distinct >= vals.length * 0.8) { gotName = true; return 'name'; }
      return 'field';
    }
    var used = {}, header = kept.map(function (k) {
      var c = cols[k], l = pretty(c.label);
      var h = c.type === 'link' ? (l ? l + ' link' : 'link') : c.type === 'img' ? (l ? l + ' image' : 'image') : l || guess(k);
      used[h] = (used[h] || 0) + 1;
      return used[h] > 1 ? h + ' ' + used[h] : h;
    });
    var idName = idAttr(items), key = '', rows = [header];
    if (idName) {
      key = pretty(idName.replace(/^data-/i, ''));
      if (key === 'id' || used[key]) key = 'item id';
      header.push(key);
    }
    data.forEach(function (r, i) {
      var row = kept.map(function (k) { return r[k] || ''; });
      if (!row.join('')) return;                   // nothing in the kept fields: an end-of-list note, a placeholder
      if (idName) row.push(clean(items[i].getAttribute(idName)));
      rows.push(row);
    });
    return { rows: rows, key: key, alike: alike / n, what: idName ? pretty(idName.replace(/^data-|[-_]?id$/gi, '')) : '' };
  }

  // Re-rank the strongest candidates by how alike their items really are (a column of unrelated page sections
  // isn't a list), then keep the best few that don't overlap.
  cands.sort(function (x, y) { return y.score - x.score; });
  var top = cands.slice(0, 15);
  top.forEach(function (cd) { var r = listRows(sample(cd.items, 40)); cd.rank = r ? cd.score * r.alike : 0; });
  top.sort(function (x, y) { return y.rank - x.rank; });
  var chosen = [], sigs = {}, contents = {};
  for (var e = 0; e < top.length && chosen.length < 3; e++) {
    var cd = top[e];
    if (!cd.rank || chosen.some(function (o) { return o !== cd.el && (o.contains(cd.el) || cd.el.contains(o)); })) continue;
    // very long lists: the newest items (each scan appends to what earlier scans collected)
    var res = listRows(cd.items.length > 2000 ? cd.items.slice(-2000) : cd.items);
    if (!res) continue;
    var content = JSON.stringify(res.rows);
    if (contents[content]) continue;               // the same list shown twice (e.g. a duplicated footer)
    contents[content] = 1;
    var label = clean(cd.el.getAttribute('aria-label'));
    var prev = cd.el.previousElementSibling;
    if (!label && prev && /^H[1-6]$/.test(prev.tagName)) label = clean(prev.textContent);
    var first = cd.items[0], what = pretty(named(first) || res.what || first.tagName.toLowerCase());
    // identifies the same list across scans, so rows found later are appended to it
    var sig = clean('list:' + cd.el.tagName + classes(cd.el, 2) + '#' + (cd.el.id || '') + '>' + first.tagName + classes(first, 1));
    sigs[sig] = (sigs[sig] || 0) + 1;
    if (sigs[sig] > 1) sig += '~' + sigs[sig];
    if (add((label ? label + ' - ' : '') + what + ' list', res.rows, 'list', sig, res.key)) chosen.push(cd.el);
  }
  return out;
})()