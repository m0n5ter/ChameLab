// Translations for the website. The pages are written in English; elements carry
// data-i18n="key" (their HTML is replaced) and data-i18n-attr="attr:key,…" (attributes),
// and i18n/<lang>.json holds the translated HTML. The language comes from ?lang=, then the
// one picked before, then the browser's languages. Loaded without defer, so the page stays
// hidden (html.i18n-pending) until it is translated and doesn't flash English first.
(() => {
  const languages = {
    en: 'English', de: 'Deutsch', es: 'Español', fr: 'Français', it: 'Italiano',
    ja: '日本語', ko: '한국어', pt: 'Português', ru: 'Русский', 'zh-Hans': '简体中文',
  };
  const base = new URL('.', document.currentScript.src);

  function supported(tag) {
    if (!tag) return null;
    const lower = tag.toLowerCase();
    if (lower.startsWith('zh')) return 'zh-Hans';
    const short = lower.split('-')[0];
    return Object.keys(languages).find(code => code.toLowerCase() === lower || code === short) || null;
  }

  function stored() {
    try { return localStorage.getItem('lang'); } catch { return null; }
  }

  const query = new URLSearchParams(location.search).get('lang');
  const lang = supported(query) || supported(stored())
    || (navigator.languages || [navigator.language]).map(supported).find(Boolean) || 'en';

  let dict = {};
  const root = document.documentElement;
  const ready = lang === 'en' ? Promise.resolve(dict) : (() => {
    root.classList.add('i18n-pending');
    // Never keep the page hidden for long if the file is slow or missing.
    const reveal = setTimeout(() => root.classList.remove('i18n-pending'), 3000);
    return fetch(new URL(`i18n/${lang}.json`, base))
      .then(r => r.ok ? r.json() : {})
      .catch(() => ({}))
      .then(loaded => { dict = loaded; clearTimeout(reveal); return dict; });
  })();

  /** The translation of key, or the English text when there is none. */
  function t(key, english) {
    return Object.hasOwn(dict, key) ? dict[key] : english;
  }

  function apply() {
    root.lang = lang;
    for (const el of document.querySelectorAll('[data-i18n]')) {
      const html = dict[el.dataset.i18n];
      if (html != null) el.innerHTML = html;
    }
    for (const el of document.querySelectorAll('[data-i18n-attr]')) {
      for (const pair of el.dataset.i18nAttr.split(',')) {
        const [attr, key] = pair.split(':');
        if (dict[key] != null) el.setAttribute(attr, dict[key]);
      }
    }
    root.classList.remove('i18n-pending');
  }

  function picker() {
    const select = document.getElementById('lang-select');
    if (!select) return;
    for (const [code, name] of Object.entries(languages)) {
      select.append(Object.assign(document.createElement('option'), { value: code, textContent: name, lang: code, selected: code === lang }));
    }
    select.addEventListener('change', () => {
      try { localStorage.setItem('lang', select.value); } catch { /* still switches for this visit */ }
      const url = new URL(location.href);
      url.searchParams.delete('lang');
      location.replace(url);
    });
  }

  // Longer translations can need more room than the navigation bar has: then it folds into
  // the Menu button, as on a phone.
  function fitNav() {
    const inner = document.querySelector('.nav-inner');
    if (!inner) return;
    root.classList.remove('nav-compact');
    if (inner.scrollWidth > inner.clientWidth) root.classList.add('nav-compact');
  }

  const loaded = new Promise(resolve => {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', resolve, { once: true });
    else resolve();
  });
  Promise.all([ready, loaded]).then(() => {
    if (lang !== 'en') apply();
    // Links for one language only, such as the Russian-speaking channels.
    for (const el of document.querySelectorAll('[data-only-lang]')) el.hidden = el.dataset.onlyLang !== lang;
    picker();
    fitNav();
    let frame = 0;
    addEventListener('resize', () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(fitNav); });
    document.fonts?.ready.then(fitNav);
  });

  window.i18n = { lang, ready: Promise.all([ready, loaded]).then(() => lang), t };
})();
