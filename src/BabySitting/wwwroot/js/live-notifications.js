// Live notifications over SignalR (same flow as WatchWise's notificationSocket.ts with Reverb):
// "notification.pushed" shows a toast and updates the bell; "notifications.changed" re-reads the unread count.
(() => {
    if (!window.signalR || !document.body.dataset.liveNotifications) return;
    const bell = document.querySelector('.notification-shortcut');
    const feed = document.querySelector('[data-live-feed]');
    const toasts = document.createElement('div');
    toasts.className = 'live-toasts'; toasts.setAttribute('aria-live', 'polite');
    document.body.append(toasts);

    const setUnread = count => {
        let dot = bell?.querySelector('i');
        if (count > 0 && bell && !dot) { dot = document.createElement('i'); bell.append(dot); }
        if (count <= 0) dot?.remove();
        if (bell) bell.setAttribute('aria-label', `Notifications, ${count} unread`);
        document.querySelectorAll('[data-live-unread-count]').forEach(el => { el.textContent = count; });
        document.querySelectorAll('[data-nav-section="notifications"]').forEach(link => {
            let badge = link.querySelector('.nav-count');
            if (count > 0 && !badge) { badge = document.createElement('span'); badge.className = 'nav-count'; link.append(badge); }
            if (badge) { badge.textContent = count; badge.hidden = count <= 0; }
        });
    };
    const refreshCount = () => fetch('/Notifications/Count', { headers: { Accept: 'application/json' }, credentials: 'same-origin' })
        .then(r => r.ok ? r.json() : null).then(data => data && setUnread(data.unread)).catch(() => {});

    const toast = n => {
        const item = document.createElement(n.url ? 'a' : 'div');
        item.className = 'live-toast';
        item.dataset.notificationCategory = n.category;
        if (n.url) item.href = n.url;
        const title = document.createElement('strong'); title.textContent = n.title;
        const body = document.createElement('span'); body.textContent = n.body;
        item.append(title, body);
        toasts.prepend(item);
        setTimeout(() => item.classList.add('leaving'), 6000);
        setTimeout(() => item.remove(), 6600);
    };
    const prepend = n => {
        if (!feed) return;
        feed.querySelector('[data-live-empty]')?.remove();
        const row = document.createElement('article');
        row.dataset.notificationCategory = n.category;
        row.className = 'card request-row notification-row unread live-new';
        const text = document.createElement('div');
        [['strong', n.title], ['span', n.body], ['span', 'Just now']].forEach(([tag, value]) => {
            const el = document.createElement(tag); el.textContent = value; if (tag === 'span') el.className = 'pet-meta'; text.append(el);
        });
        const side = document.createElement('div'); side.className = 'request-row-side';
        const chip = document.createElement('span'); chip.className = 'status-chip yellow'; chip.textContent = 'New'; side.append(chip);
        if (n.url) { const link = document.createElement('a'); link.className = 'btn btn-dark btn-sm'; link.href = n.url; link.textContent = 'Open'; side.append(link); }
        row.append(text, side);
        feed.prepend(row);
    };

    const connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/notifications')
        // Back off like WatchWise: 2s, doubling, capped at 60s, and keep trying.
        .withAutomaticReconnect({ nextRetryDelayInMilliseconds: ctx => Math.min(2000 * 2 ** ctx.previousRetryCount, 60000) })
        .build();
    connection.on('notification.pushed', ({ notification, silent }) => {
        // Pages (e.g. combined care) can refresh themselves when an update for them arrives.
        document.dispatchEvent(new CustomEvent('careconnect:notification', { detail: notification }));
        if (allowedCategories && !allowedCategories.has(notification.category)) return;
        prepend(notification);
        if (!silent) toast(notification);
        refreshCount();
    });
    let allowedCategories;
    const refreshPreferences = () => fetch('/Notifications/Preferences', { credentials: 'same-origin', cache: 'no-store' })
        .then(r => r.ok ? r.json() : null).then(data => {
            if (!data) return;
            allowedCategories = new Set(data.categories);
            document.querySelectorAll('[data-notification-category]').forEach(row => {
                if (!allowedCategories.has(row.dataset.notificationCategory)) row.remove();
            });
        }).catch(() => {});
    connection.on('notifications.changed', data => {
        refreshCount();
        if (data?.preferencesChanged) refreshPreferences();
    });
    connection.onreconnected(() => { refreshCount(); refreshPreferences(); });
    const start = (delay = 2000) => connection.start().then(refreshCount).catch(() => setTimeout(() => start(Math.min(delay * 2, 60000)), delay));
    start();
})();
