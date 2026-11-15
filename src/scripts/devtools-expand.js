(function () {
  var MORE = /^(\.\.\.|…)?\s*((see|read|show|view)\s+)?more\s*(\.\.\.|…)?$|^(展开|全文|展开全文|查看全文|更多)$/i;
  var bs = document.querySelectorAll('button[aria-expanded=false], [role=button][aria-expanded=false]'), n = 0;
  for (var i = 0; i < bs.length && n < 300; i++) {
    var b = bs[i];
    if (b.tagName === 'A' || b.disabled || b.hasAttribute('aria-haspopup') || !b.getClientRects().length) continue;
    if (b.closest('nav, header, footer, [role=navigation], [role=banner], [role=menu], [role=menubar]')) continue;
    if (!MORE.test((b.textContent || '').trim()) && !MORE.test((b.getAttribute('aria-label') || '').trim())) continue;
    b.click();
    n++;
  }
  return n;
})()