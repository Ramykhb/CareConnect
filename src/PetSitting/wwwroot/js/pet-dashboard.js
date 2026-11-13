(() => {
    'use strict';
    const board = document.querySelector('[data-dashboard]');
    const search = document.getElementById('dashboard-search');
    const feedback = document.getElementById('search-feedback');
    const featureDialog = document.getElementById('feature-dialog');
    const sectionNames = new Set(['overview', 'requests', 'bookings', 'pets', 'payments', 'sitters', 'notifications', 'earnings', 'reports', 'availability', 'messages', 'reviews']);
    const cards = board ? [...board.children].filter(el => el.hasAttribute('data-sections')) : [];
    const isSitter = board?.dataset.dashboard === 'sitter';
    // These are planned care workflows. Keep their status explicit; never simulate a payment or booking.
    const featureCopy = {
        'Messages': 'Messaging is coming soon. Your existing bookings and care details are available on your dashboard.',
        'Create a sitting request': 'Care request creation is coming soon. In the meantime, add your pets and their care instructions so everything is ready for their next stay.',
        'Respond to a request': 'Request responses and booking confirmation are coming soon. This request is open; reviewing it does not accept or reserve it.',
        'Manage request': 'Request editing and offer management are coming soon. Your existing request details are shown on the dashboard.',
        'Update pricing': 'Sitter profile and pricing management are coming soon. Your current profile details have not been changed.',
        'Request payout': 'Payouts are coming soon. Your earnings show recorded successful payments; no payout has been requested.',
        'Complete payment': 'Payment processing is coming soon. Your booking is still awaiting payment, and no charge has been made.',
        'Help center': 'For now, you can manage your profile in Settings, browse your dashboard, and keep your pet profiles up to date. More support tools are on the way.'
    };
    function showSection() {
        if (!board) return;
        const portalRoutes = { '#earnings': '/Sitter/Earnings', '#reports': '/Sitter/Analytics', '#reviews': '/Sitter/Reviews', '#notifications': '/Sitter/Notifications' };
        if (isSitter && portalRoutes[window.location.hash]) { window.location.replace(portalRoutes[window.location.hash]); return; }
        if (isSitter && window.location.hash === '#bookings') { const id = new URLSearchParams(window.location.search).get('booking'); window.location.replace(id ? '/SitterBookings/Details/' + encodeURIComponent(id) : '/SitterBookings'); return; }
        if (isSitter && window.location.hash === '#availability') { window.location.replace('/SitterAvailability'); return; }
        if (isSitter && window.location.hash === '#requests') { window.location.replace('/SitterRequests'); return; }
        const hash = window.location.hash.slice(1);
        const section = sectionNames.has(hash) ? hash : 'overview';
        board.dataset.view = section;
        for (const card of cards) {
            const allowed = card.dataset.sections.split(' ');
            card.hidden = !allowed.includes(section) || (isSitter && section === 'overview' && card.classList.contains('bookings-card'));
        }
        document.querySelectorAll('[data-tab]').forEach(tab => {
            const active = tab.dataset.tab === section;
            tab.classList.toggle('active', active);
            if (active) tab.setAttribute('aria-current', 'page'); else tab.removeAttribute('aria-current');
        });
        document.querySelectorAll('[data-nav-section]').forEach(link => {
            const active = link.dataset.navSection === section;
            link.classList.toggle('active', active);
            if (active) link.setAttribute('aria-current', 'page'); else link.removeAttribute('aria-current');
        });
        applySearch();
    }
    function applySearch() {
        if (!board || !search) return;
        const term = search.value.trim().toLocaleLowerCase();
        let count = 0;
        board.classList.toggle('is-searching', !!term);
        board.querySelectorAll('.searchable').forEach(row => {
            const matches = !term || (row.dataset.search || row.textContent).toLocaleLowerCase().includes(term);
            row.hidden = !matches;
            if (matches && !row.closest('[data-sections]')?.hidden) count++;
        });
        if (feedback) {
            feedback.hidden = !term;
            feedback.textContent = count ? `${count} matching ${count === 1 ? 'item' : 'items'} in this view.` : 'No matching items in this view. Try another search or dashboard tab.';
        }
    }
    window.addEventListener('hashchange', showSection);
    search?.addEventListener('input', applySearch);
    document.addEventListener('keydown', event => {
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k' && search) { event.preventDefault(); search.focus(); }
    });
    document.querySelector('.mobile-nav-toggle')?.addEventListener('click', event => {
        const open = document.body.classList.toggle('nav-open');
        event.currentTarget.setAttribute('aria-expanded', String(open));
    });
    document.querySelectorAll('[data-nav-section]').forEach(link => link.addEventListener('click', () => {
        document.body.classList.remove('nav-open');
        document.querySelector('.mobile-nav-toggle')?.setAttribute('aria-expanded', 'false');
    }));
    document.addEventListener('click', event => {
        const close = event.target.closest('[data-close]');
        if (close) close.closest('dialog')?.close();
        const feature = event.target.closest('[data-feature]');
        if (feature && featureDialog) {
            const name = feature.dataset.feature;
            if (name === 'Update pricing') { window.location.href = '/Sitter/Settings#services-settings'; return; }
            if (name === 'Request payout') { window.location.href = '/Sitter/Earnings'; return; }
            document.getElementById('feature-title').textContent = name;
            document.getElementById('feature-description').textContent = featureCopy[name] || 'This feature is coming soon. Your existing data has not been changed.';
            featureDialog.showModal();
        }
        const trigger = event.target.closest('[data-dialog]');
        if (trigger) document.getElementById(trigger.dataset.dialog)?.showModal();
        if (!event.target.closest('.account-menu')) document.querySelectorAll('.account-menu[open]').forEach(menu => menu.open = false);
    });
    document.querySelectorAll('dialog').forEach(dialog => dialog.addEventListener('click', event => {
        if (event.target !== dialog) return;
        const rect = dialog.getBoundingClientRect();
        if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) dialog.close();
    }));
    document.querySelectorAll('[data-export]').forEach(button => button.addEventListener('click', () => {
        const rows = [...document.querySelectorAll('#report-data [data-month]')];
        const year = document.querySelector('.year-pill')?.textContent.trim() || '';
        const csv = ['Year,Month,Hours booked,Received USD', ...rows.map(row => `${year},${row.dataset.month},${row.dataset.hours},${row.dataset.paid}`)].join('\r\n');
        const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8;' }));
        const link = document.createElement('a');
        link.href = url;
        link.download = `pawpal-report-${year}.csv`;
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }));
    showSection();
    const bookingId = new URLSearchParams(window.location.search).get('booking');
    const bookingDialog = bookingId && document.getElementById(`booking-${bookingId}`);
    if (bookingDialog?.matches('dialog')) bookingDialog.showModal();
})();
