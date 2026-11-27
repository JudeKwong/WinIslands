/* ============================================================
   WinIslands 官网 — main.js
   ============================================================ */
'use strict';

/* ---------- 导航（移动端） ---------- */
(function () {
  const toggle = document.getElementById('navToggle');
  const links = document.getElementById('navLinks');
  if (toggle && links) {
    toggle.addEventListener('click', function () {
      links.classList.toggle('open');
    });
    links.addEventListener('click', function (e) {
      if (e.target.tagName === 'A') links.classList.remove('open');
    });
  }

  /* 导航加滚动阴影 */
  const nav = document.getElementById('nav');
  const onScroll = function () {
    if (nav) nav.style.background = window.scrollY > 8
      ? 'rgba(5,7,13,.82)'
      : 'rgba(5,7,13,.55)';
  };
  window.addEventListener('scroll', onScroll, { passive: true });
  onScroll();
})();

/* ---------- 滚动渐入 ---------- */
(function () {
  const els = document.querySelectorAll('.card, .dl-card, .shot, .spec, .section-head, .api-text, .api-code');
  if (!('IntersectionObserver' in window)) {
    els.forEach(function (el) { el.classList.add('in'); });
    return;
  }
  els.forEach(function (el) { el.classList.add('reveal'); });
  const io = new IntersectionObserver(function (entries) {
    entries.forEach(function (entry) {
      if (entry.isIntersecting) {
        entry.target.classList.add('in');
        io.unobserve(entry.target);
      }
    });
  }, { threshold: 0.12 });
  els.forEach(function (el) { io.observe(el); });
})();

/* ---------- 灵动岛演示 ---------- */
(function () {
  var island = document.getElementById('demoIsland');
  var fill = document.getElementById('lyrFill');
  var play = document.getElementById('demoPlay');
  if (!island || !fill) return;

  var pauseSVG = '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><rect x="6" y="4" width="4" height="16" rx="1"/><rect x="14" y="4" width="4" height="16" rx="1"/></svg>';
  var playSVG = '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><polygon points="6 3 20 12 6 21 6 3"/></svg>';

  var playing = true;
  if (play) {
    play.innerHTML = pauseSVG;
    play.addEventListener('click', function (e) {
      e.stopPropagation();
      playing = !playing;
      play.innerHTML = playing ? pauseSVG : playSVG;
    });
  }

  /* 卡拉OK逐字歌词：从 0 到 100% 循环推进 */
  var startTime = null;
  var CYCLE_MS = 9000;
  function frame(ts) {
    if (startTime === null) startTime = ts;
    var t = (ts - startTime) % CYCLE_MS;
    var p = Math.min(1, Math.max(0, t / CYCLE_MS));
    if (playing) {
      fill.style.clipPath = 'inset(0 ' + (100 - p * 100).toFixed(2) + '% 0 0)';
    }
    if (t > CYCLE_MS - 200) startTime = ts;
    requestAnimationFrame(frame);
  }
  requestAnimationFrame(frame);
})();

/* ---------- 页脚年份 ---------- */
(function () {
  const y = document.getElementById('year');
  if (y) y.textContent = new Date().getFullYear();
})();
