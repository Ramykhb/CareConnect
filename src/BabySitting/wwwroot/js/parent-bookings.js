(() => {
    'use strict';
    const list = document.querySelector('[data-ob-list]');
    const detail = document.querySelector('[data-ob-detail]');
    const target = list || detail;
    let dirty = false;
    if (detail) detail.addEventListener('input', () => { dirty = true; });
    function openSection() {
        if (!location.hash) return;
        const section = document.getElementById(location.hash.slice(1));
        if (section?.matches('details')) section.open = true;
    }
    window.addEventListener('hashchange', openSection);
    detail?.addEventListener('click', event => {
        const link = event.target.closest('a[href^="#"]');
        if (link) {
            const section = document.getElementById(link.hash.slice(1));
            if (section?.matches('details')) section.open = true;
        }
    });
    openSection();
    const filters = document.querySelector('.ob-filters');
    filters?.querySelectorAll('select').forEach(select => select.addEventListener('change', () => filters.requestSubmit()));
    const search = filters?.querySelector('input[type=search]');
    document.addEventListener('keydown', event => {
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k' && search) { event.preventDefault(); search.focus(); }
    });
    if (!target) return;
    let failures = 0;
    const refresh = async () => {
        const editing = detail && (dirty || detail.contains(document.activeElement) || detail.querySelector('details[open]'));
        if (!document.hidden && !editing && !(list && list.contains(document.activeElement))) {
            try {
                const response = await fetch(target.dataset.refreshUrl, { headers: { 'X-Requested-With': 'XMLHttpRequest' } });
                if (!response.ok || response.redirected) throw new Error();
                const html = await response.text();
                if (target.innerHTML !== html) target.innerHTML = html;
                if (list) list.querySelectorAll('[data-count]').forEach(count => {
                    const badge = document.querySelector(`[data-ob-count="${count.dataset.count}"]`);
                    if (badge) badge.textContent = count.textContent;
                });
                document.querySelectorAll('[data-ob-sync]').forEach(label => {
                    label.classList.remove('offline');
                    label.querySelector('span').textContent = 'Synced just now';
                });
                failures = 0;
            } catch {
                failures++;
                document.querySelectorAll('[data-ob-sync]').forEach(label => {
                    label.classList.add('offline');
                    label.querySelector('span').textContent = 'Reconnecting…';
                });
            }
        }
        setTimeout(refresh, Math.min(60000, 15000 * (failures + 1)));
    };
    setTimeout(refresh, 15000);
})();
