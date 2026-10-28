const toggle = document.querySelector('.menu-toggle');
const nav = document.querySelector('#navigation');
if (toggle && nav) {
  const close = () => { toggle.setAttribute('aria-expanded', 'false'); nav.classList.remove('is-open'); };
  toggle.addEventListener('click', () => {
    const open = toggle.getAttribute('aria-expanded') !== 'true';
    toggle.setAttribute('aria-expanded', String(open)); nav.classList.toggle('is-open', open);
  });
  nav.addEventListener('click', event => { if (event.target.closest('a')) close(); });
  document.addEventListener('keydown', event => { if (event.key === 'Escape') { close(); toggle.focus(); } });
  document.addEventListener('click', event => { if (!event.target.closest('.header-inner')) close(); });
}

const downloadLinks = [...document.querySelectorAll('a[data-download="windows"]')];
if (downloadLinks.length) {
  fetch('https://api.github.com/repos/csgdV2/WavyMusic/releases/latest', {
    headers: {Accept:'application/vnd.github+json'}, signal:AbortSignal.timeout(6000),
  }).then(response => { if (!response.ok) throw new Error('Release metadata unavailable'); return response.json(); })
    .then(release => {
      const executables = (release.assets || []).filter(asset => /\.exe$/i.test(asset.name) && !/arm64|armv7/i.test(asset.name));
      const asset = executables.find(asset => /setup|install/i.test(asset.name)) || executables[0];
      if (!asset) return;
      const url = new URL(asset.browser_download_url);
      if (url.protocol !== 'https:' || url.hostname !== 'github.com' || !url.pathname.startsWith('/csgdV2/WavyMusic/releases/download/')) return;
      for (const link of downloadLinks) link.href = url.href;
    }).catch(() => {});
}

const sectionLinks = [...document.querySelectorAll('.legal-nav a[href^="#"]')];
const legalSections = sectionLinks.map(link => document.getElementById(decodeURIComponent(link.hash.slice(1)))).filter(Boolean);
if (legalSections.length) {
  let framePending = false;
  const highlightSection = () => {
    framePending = false;
    const readingLine = Math.min(180, innerHeight * 0.3);
    let current = legalSections[0];
    for (const section of legalSections) {
      if (section.getBoundingClientRect().top <= readingLine) current = section;
      else break;
    }
    if (document.documentElement.scrollHeight > innerHeight + 10 &&
        scrollY + innerHeight >= document.documentElement.scrollHeight - 4) current = legalSections.at(-1);
    for (const link of sectionLinks) {
      if (link.hash === '#' + current.id) link.setAttribute('aria-current', 'location');
      else link.removeAttribute('aria-current');
    }
  };
  const scheduleHighlight = () => {
    if (!framePending) { framePending = true; requestAnimationFrame(highlightSection); }
  };
  addEventListener('scroll', scheduleHighlight, {passive: true});
  addEventListener('resize', scheduleHighlight);
  addEventListener('hashchange', scheduleHighlight);
  addEventListener('load', scheduleHighlight);
  highlightSection();
}
