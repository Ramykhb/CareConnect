(() => {
    'use strict';
    const page = document.querySelector('[data-requests-page]');
    if (!page) return;
    const dialog = document.getElementById('request-decision-dialog');
    const form = document.getElementById('request-decision-form');
    const searchForm = document.getElementById('request-search-form');
    const search = searchForm.elements.search;
    const globalSearch = document.getElementById('dashboard-search');
    const sync = page.querySelector('[data-sync-status]');
    const toast = page.querySelector('.incoming-toast');
    let knownNew = new Set(page.dataset.newIds.split(',').filter(Boolean));
    let controller;
    let searchTimer;
    let toastTimer;
    let submitting = false;
    if (globalSearch) { globalSearch.value = search.value; globalSearch.placeholder = 'Search requests, owners…'; globalSearch.setAttribute('aria-label', 'Search requests'); }

    function syncLabel(text, failed = false) {
        sync.classList.toggle('offline', failed);
        sync.querySelector('span').textContent = text;
    }
    function notify(title, body) {
        toast.querySelector('[data-toast-title]').textContent = title;
        toast.querySelector('[data-toast-body]').textContent = body;
        toast.hidden = false;
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => { toast.hidden = true; }, 8000);
    }
    async function load(url, historyMode) {
        if (submitting || dialog.open) return;
        controller?.abort();
        const request = new AbortController();
        controller = request;
        const source = new URL(url, window.location.origin);
        source.searchParams.delete('confirm');
        const updates = new URL(page.dataset.updatesUrl, window.location.origin);
        updates.search = source.search;
        if (historyMode) page.setAttribute('aria-busy', 'true');
        try {
            const response = await fetch(updates, { signal: request.signal, credentials: 'same-origin', cache: 'no-store' });
            if (!response.ok || response.redirected) throw new Error('Request update failed');
            const html = await response.text();
            if (request.signal.aborted) return;
            const parsed = new DOMParser().parseFromString(html, 'text/html');
            const fragment = parsed.querySelector('[data-inbox-fragment]');
            if (!fragment) throw new Error('Invalid inbox update');
            const previousScroll = page.querySelector('[data-request-list]').scrollTop;
            const focusHref = page.contains(document.activeElement) ? document.activeElement.closest('a')?.href : null;
            for (const selector of ['[data-request-tabs]', '[data-request-list]', '[data-request-detail]']) {
                const next = fragment.querySelector(selector);
                if (!next) throw new Error('Incomplete inbox update');
                page.querySelector(selector).replaceWith(next);
            }
            page.querySelector('[data-request-list]').scrollTop = previousScroll;
            if (focusHref) [...page.querySelectorAll('a')].find(link => link.href === focusHref)?.focus({ preventScroll: true });
            const currentNew = new Set(fragment.dataset.newIds.split(',').filter(Boolean));
            const incoming = [...currentNew].filter(id => !knownNew.has(id));
            knownNew = currentNew;
            if (incoming.length) {
                // New requests may not be in the currently selected status/search view.
                const first = [...page.querySelectorAll('[data-request-id]')].find(row => incoming.includes(row.dataset.requestId));
                const title = first ? `New request from ${first.dataset.owner}` : `${incoming.length} new ${incoming.length === 1 ? 'request' : 'requests'} received`;
                notify(title, first?.dataset.petSummary || 'Open the New tab to review the latest requests.');
                page.querySelector('[data-inbox-banner]').textContent = `${title} — this list updates automatically, no refresh needed.`;
            }
            if (!incoming.length && page.dataset.decisionMessage !== 'True') {
                const count = Number(fragment.dataset.newCount);
                page.querySelector('[data-inbox-banner]').textContent = count ? `You have ${count} new ${count === 1 ? 'request' : 'requests'} — this list updates automatically, no refresh needed.` : 'You’re all caught up — new requests will appear here automatically.';
            }
            page.dataset.selectedId = fragment.dataset.selectedId;
            page.dataset.filter = fragment.dataset.filter;
            searchForm.elements.status.value = fragment.dataset.filter;
            const navLink = document.querySelector('[data-nav-section="requests"]');
            let badge = navLink?.querySelector('.nav-count');
            if (!badge && Number(fragment.dataset.newCount) > 0) { badge = document.createElement('span'); badge.className = 'nav-count'; navLink?.append(badge); }
            if (badge) { badge.textContent = fragment.dataset.newCount; badge.hidden = Number(fragment.dataset.newCount) === 0; }
            if (fragment.dataset.selectedId) source.searchParams.set('id', fragment.dataset.selectedId); else source.searchParams.delete('id');
            if (historyMode === 'push') history.pushState(null, '', source); else history.replaceState(null, '', source);
            syncLabel('Live · synced just now');
            page.querySelector('[data-last-synced]').textContent = 'Just now';
        } catch (error) {
            if (error.name === 'AbortError') return;
            syncLabel('Updates paused · retrying', true);
            if (historyMode) notify('Couldn’t load requests', 'Please try again. Your saved decisions have not changed.');
        } finally {
            if (controller === request) { page.removeAttribute('aria-busy'); controller = null; }
        }
    }
    function currentUrl() {
        const url = new URL(window.location.href);
        url.searchParams.delete('confirm');
        url.searchParams.set('status', page.dataset.filter);
        if (page.dataset.selectedId) url.searchParams.set('id', page.dataset.selectedId); else url.searchParams.delete('id');
        return url;
    }
    function searchChanged(input) {
        const value = input.value.slice(0, 150);
        search.value = value;
        if (globalSearch) globalSearch.value = value;
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => {
            searchTimer = null;
            const url = currentUrl();
            if (value) url.searchParams.set('search', value); else url.searchParams.delete('search');
            url.searchParams.delete('id');
            load(url, 'replace');
        }, 250);
    }
    search.addEventListener('input', () => searchChanged(search));
    globalSearch?.addEventListener('input', () => searchChanged(globalSearch));
    searchForm.addEventListener('submit', event => { event.preventDefault(); clearTimeout(searchTimer); searchTimer = null; const url = currentUrl(); url.searchParams.set('search', search.value); url.searchParams.delete('id'); load(url, 'replace'); });
    page.addEventListener('click', event => {
        const decision = event.target.closest('[data-decision]');
        if (decision) {
            event.preventDefault();
            controller?.abort();
            page.removeAttribute('aria-busy');
            const accept = decision.dataset.decision === 'accept';
            form.elements.RequestId.value = decision.dataset.request;
            form.elements.Version.value = decision.dataset.version;
            form.elements.Decision.value = decision.dataset.decision;
            document.getElementById('decision-title').textContent = accept ? 'Accept this request?' : 'Decline this request?';
            document.getElementById('decision-description').textContent = accept ? `You’re committing to ${decision.dataset.summary} for ${decision.dataset.amount}. The parent will be notified and a booking will be created.` : 'The parent will be notified. This request will stay available to other sitters.';
            form.querySelector('[data-confirm-decision]').textContent = accept ? 'Accept request' : 'Decline request';
            dialog.querySelector('.decision-icon use').setAttribute('href', accept ? '#icon-check' : '#icon-close');
            const back = new URL(decision.href);
            back.searchParams.delete('confirm');
            dialog.querySelector('[data-cancel-decision]').href = back;
            if (!dialog.open) dialog.showModal();
            return;
        }
        const link = event.target.closest('a[data-inbox-link]');
        if (link && !event.ctrlKey && !event.metaKey && !event.shiftKey && event.button === 0) { event.preventDefault(); clearTimeout(searchTimer); searchTimer = null; load(link.href, 'push'); }
    });
    dialog.querySelector('[data-cancel-decision]').addEventListener('click', event => { event.preventDefault(); dialog.close(); });
    form.addEventListener('submit', event => {
        if (submitting) { event.preventDefault(); return; }
        submitting = true;
        controller?.abort();
        form.querySelector('[data-confirm-decision]').disabled = true;
        form.querySelector('[data-confirm-decision]').textContent = 'Sending decision…';
    });
    page.querySelector('[data-dismiss-toast]').addEventListener('click', () => { toast.hidden = true; clearTimeout(toastTimer); });
    window.addEventListener('popstate', () => {
        if (dialog.open) dialog.close();
        clearTimeout(searchTimer); searchTimer = null;
        const value = new URL(window.location.href).searchParams.get('search') || '';
        search.value = value;
        if (globalSearch) globalSearch.value = value;
        load(window.location.href);
    });
    window.addEventListener('pageshow', () => {
        submitting = false;
        form.querySelector('[data-confirm-decision]').disabled = false;
        form.querySelector('[data-confirm-decision]').textContent = form.elements.Decision.value === 'accept' ? 'Accept request' : 'Decline request';
    });
    if (dialog.open) { dialog.close(); dialog.showModal(); }
    dialog.addEventListener('close', () => {
        const url = new URL(window.location.href);
        if (url.searchParams.has('confirm')) { url.searchParams.delete('confirm'); history.replaceState(null, '', url); }
    });
    setInterval(() => { if (!document.hidden && !searchTimer && !controller) load(currentUrl()); }, 15000);
})();
