(function () {
  var se = document.scrollingElement || document.documentElement, t = se;
  if (se.scrollHeight - se.clientHeight < 50) {
    var best = null, area = 0, all = document.body ? document.body.getElementsByTagName('*') : [];
    for (var i = 0; i < all.length; i++) {
      var el = all[i];
      if (el.scrollHeight - el.clientHeight > 50 && el.clientHeight > 100) {
        var oy = getComputedStyle(el).overflowY;
        if (oy === 'auto' || oy === 'scroll' || oy === 'overlay') {
          var a = el.clientWidth * el.clientHeight;
          if (a > area) { area = a; best = el; }
        }
      }
    }
    if (best) t = best;
  }
  var before = t.scrollTop;
  t.scrollTo({ top: before + Math.round(t.clientHeight * 0.85), behavior: 'instant' });
  return { moved: Math.round(t.scrollTop - before), atEnd: t.scrollTop + t.clientHeight >= t.scrollHeight - 4 };
})()