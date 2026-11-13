(() => {
    'use strict';
    document.querySelectorAll('[data-portal-dialog]').forEach(button => button.addEventListener('click', () => document.getElementById(button.dataset.portalDialog)?.showModal()));
    let audio;
    async function sound() {
        const Audio = window.AudioContext || window.webkitAudioContext;
        if (!Audio) return;
        audio ||= new Audio(); await audio.resume();
        const tone = audio.createOscillator(), gain = audio.createGain();
        tone.connect(gain); gain.connect(audio.destination); tone.frequency.value = 740;
        gain.gain.setValueAtTime(0.06, audio.currentTime); gain.gain.exponentialRampToValueAtTime(0.001, audio.currentTime + .18);
        tone.start(); tone.stop(audio.currentTime + .18);
    }
    document.querySelectorAll('[data-test-alert-sound]').forEach(button => button.addEventListener('click', () => sound().catch(() => {})));
    const page = document.querySelector('[data-notifications-page]'); if (!page) return;
    let known = new Set(page.dataset.requestIds.split(',').filter(Boolean)), busy = false;
    async function refresh(url, navigate = false) {
        if (busy) return; busy = true;
        const target = new URL(page.dataset.feedUrl, location.origin), source = new URL(url, location.origin); target.search = source.search;
        const status = page.querySelector('[data-alert-sync] span');
        try {
            const response = await fetch(target, { credentials: 'same-origin', cache: 'no-store' });
            if (!response.ok || response.redirected) throw new Error('Unavailable');
            const parsed = new DOMParser().parseFromString(await response.text(), 'text/html');
            const feed = parsed.querySelector('[data-notification-feed]'); if (!feed) throw new Error('Missing feed');
            for (const selector of ['[data-notification-tabs]', '[data-notification-list]']) page.querySelector(selector).replaceWith(feed.querySelector(selector));
            const ids = new Set(feed.dataset.requestIds.split(',').filter(Boolean));
            if (feed.dataset.sound === 'true' && [...ids].some(id => !known.has(id)) && audio?.state === 'running') sound().catch(() => {});
            known = ids;
            const badge = document.querySelector('[data-nav-section="notifications"] .nav-count');
            if (badge) { badge.textContent = feed.dataset.unread; badge.hidden = Number(feed.dataset.unread) === 0; }
            if (navigate) history.pushState(null, '', source);
            status.textContent = 'Live · synced just now';
        } catch { status.textContent = 'Updates paused · retrying'; }
        finally { busy = false; }
    }
    page.addEventListener('click', event => {
        const link = event.target.closest('[data-alert-link]');
        if (link && !event.ctrlKey && !event.metaKey && !event.shiftKey) { event.preventDefault(); refresh(link.href, true); }
    });
    window.addEventListener('popstate', () => refresh(location.href));
    setInterval(() => { if (!document.hidden) refresh(location.href); }, 15000);
})();
