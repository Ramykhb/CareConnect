(() => {
    const button = document.querySelector('[data-copy-care-code]');
    const code = document.querySelector('#cc-link-code');
    const status = document.querySelector('[data-copy-care-status]');
    if (!button || !code || !status) return;
    button.addEventListener('click', async () => {
        try {
            await navigator.clipboard.writeText(code.value);
            status.textContent = 'Code copied. Ready for the other app.';
        } catch {
            code.focus();
            code.select();
            status.textContent = 'Select the code above and copy it.';
        }
    });
})();

// Live status: refresh the linked-care list when the other app sends an update, and keep
// checking briefly while one of our own changes is still syncing.
(() => {
    const section = () => document.querySelector('.cc-linked');
    if (!section()) return;
    let busy = false, polls = 0;
    const syncing = () => !!document.querySelector('.cc-linked [data-cc-syncing]');
    const refresh = async () => {
        if (busy) return;
        busy = true;
        try {
            const response = await fetch(location.href, { credentials: 'same-origin', cache: 'no-store' });
            if (!response.ok) return;
            const next = new DOMParser().parseFromString(await response.text(), 'text/html').querySelector('.cc-linked');
            const current = section();
            if (next && current && next.innerHTML !== current.innerHTML) current.replaceWith(next);
        } catch { /* the next update or a reload will catch up */ }
        finally { busy = false; }
    };
    document.addEventListener('careconnect:notification', e => {
        if ((e.detail?.url || '').toLowerCase().startsWith('/combinedcare')) refresh();
    });
    document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
    const timer = setInterval(() => {
        if (!syncing() || ++polls > 24) { clearInterval(timer); return; }
        refresh();
    }, 5000);
})();
