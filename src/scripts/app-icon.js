(function () {
  var best = null, bestSize = -1;
  var links = document.querySelectorAll('link[rel~=icon], link[rel~=apple-touch-icon], link[rel~=apple-touch-icon-precomposed]');
  for (var i = 0; i < links.length; i++) {
    var l = links[i];
    if (!l.href || /\.svg(\?|#|$)/i.test(l.href) || l.type === 'image/svg+xml') continue;
    var m = /(\d+)x(\d+)/i.exec(l.getAttribute('sizes') || '');
    var s = m ? parseInt(m[1], 10) : (/apple-touch/i.test(l.rel) ? 180 : 32);
    if (s > bestSize) { best = l.href; bestSize = s; }
  }
  return best || (location.origin + '/favicon.ico');
})()