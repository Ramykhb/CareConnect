// Sitter booking page: count down to the care start time and reload when it arrives, so "Start care" appears without F5.
(() => {
    'use strict';
    const box = document.querySelector('[data-care-countdown]');
    if (!box) return;
    const startsAt = Number(box.dataset.careCountdown);
    const label = box.querySelector('[data-countdown-text]');
    const tick = () => {
        const left = Math.max(0, startsAt - Date.now());
        if (left === 0) { label.textContent = 'Care can start now. Opening Start care…'; setTimeout(() => location.reload(), 1500); return; }
        const h = Math.floor(left / 3600000), m = Math.floor(left % 3600000 / 60000), s = Math.floor(left % 60000 / 1000);
        label.textContent = 'Start care unlocks in ' + (h > 0 ? h + ' h ' : '') + (h > 0 || m > 0 ? m + ' min ' : '') + s + ' s. This page refreshes by itself.';
        setTimeout(tick, 1000);
    };
    tick();
})();
