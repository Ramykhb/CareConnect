(() => {
    'use strict';
    const search = document.getElementById('dashboard-search');
    if (search?.form?.id === 'booking-search-form') { search.placeholder = 'Search bookings, pets…'; search.setAttribute('aria-label', 'Search bookings'); }
    const confirmation = document.getElementById('booking-status-confirm');
    let pendingForm, pendingButton;
    document.querySelectorAll('[data-status-confirm]').forEach(form => form.addEventListener('submit', event => {
        if (form.dataset.confirmed === 'true' || !confirmation) return;
        event.preventDefault(); pendingForm = form; pendingButton = event.submitter;
        confirmation.querySelector('[data-status-description]').textContent = form.dataset.statusConfirm;
        confirmation.showModal();
    }));
    confirmation?.querySelector('[data-confirm-status]').addEventListener('click', () => {
        if (!pendingForm) return;
        pendingForm.dataset.confirmed = 'true'; confirmation.close(); pendingForm.requestSubmit(pendingButton);
    });
    document.querySelectorAll('[data-open-dialog]').forEach(button => button.addEventListener('click', () => document.getElementById(button.dataset.openDialog)?.showModal()));
    document.querySelectorAll('[data-care-toggle]').forEach(input => input.addEventListener('change', () => input.form.requestSubmit()));
    document.querySelectorAll('[data-preserve-scroll]').forEach(form => form.addEventListener('submit', () => sessionStorage.setItem('careconnect-care-scroll', JSON.stringify({ path: location.pathname, y: window.scrollY }))));
    try { const saved = JSON.parse(sessionStorage.getItem('careconnect-care-scroll')); if (saved?.path === location.pathname) { window.scrollTo(0, saved.y); sessionStorage.removeItem('careconnect-care-scroll'); } } catch { /* No saved scroll position. */ }
    document.querySelectorAll('[data-update-form]').forEach(form => {
        const file = form.querySelector('[data-care-photo]'), preview = form.querySelector('[data-care-preview]'), error = form.querySelector('.photo-error');
        let url;
        file.addEventListener('change', () => {
            if (url) URL.revokeObjectURL(url);
            error.hidden = true; preview.hidden = true;
            const photo = file.files[0]; if (!photo) return;
            if (photo.size > 5242880 || !['image/jpeg', 'image/png', 'image/webp'].includes(photo.type)) { error.textContent = 'Choose a JPEG, PNG or WebP photo up to 5 MB.'; error.hidden = false; file.value = ''; return; }
            url = URL.createObjectURL(photo); preview.src = url; preview.hidden = false;
        });
        form.addEventListener('submit', event => {
            if (!form.elements.Message.value.trim() && !file.files.length) { event.preventDefault(); error.textContent = 'Write an update or add a photo first.'; error.hidden = false; return; }
            form.querySelector('button[type="submit"]').disabled = true;
        });
    });
    const page = document.querySelector('[data-availability-page]');
    if (!page) return;
    const editor = page.querySelector('[data-schedule-editor]'), form = editor.querySelector('[data-schedule-form]'), dialog = document.getElementById('availability-editor-dialog');
    const field = name => form.elements.namedItem('Editor.' + name);
    const checkbox = name => [...form.querySelectorAll('input')].find(el => el.name === 'Editor.' + name && el.type === 'checkbox');
    function choice(available) { [...form.querySelectorAll('input[name="Editor.Available"]')].forEach(el => el.checked = el.value === String(available)); }
    let previewTimer, previewController;
    function updateFields() {
        form.querySelector('[data-range-fields]').hidden = !checkbox('ApplyRange').checked;
        form.querySelector('[data-time-fields]').hidden = checkbox('AllDay').checked;
        const start = field('StartDate').value;
        if (!checkbox('ApplyRange').checked) field('EndDate').value = start;
        field('EndDate').min = start;
        clearTimeout(previewTimer);
        previewTimer = setTimeout(checkConflicts, 250);
    }
    async function checkConflicts() {
        if (!field('Version').value || !form.checkValidity()) return;
        previewController?.abort(); previewController = new AbortController();
        const params = new URLSearchParams();
        // Keep only the first value for checkboxes: checked=true precedes their hidden false input.
        for (const [key, value] of new FormData(form)) if (key.startsWith('Editor.') && !params.has(key)) params.append(key, value);
        const target = new URL(form.dataset.previewUrl, location.origin); target.search = params;
        try {
            const result = await fetch(target, { signal: previewController.signal, credentials: 'same-origin', cache: 'no-store' });
            if (!result.ok) return;
            const data = await result.json();
            editor.querySelector('[data-affected-message] span').textContent = data.affected ? `${data.affected} active ${data.affected === 1 ? 'booking is' : 'bookings are'} affected. Choose different hours.` : 'No bookings affected.';
            form.querySelector('[data-save-schedule]').disabled = data.affected > 0;
        } catch (error) { if (error.name !== 'AbortError') editor.querySelector('[data-affected-message] span').textContent = 'Booking conflicts will be checked when you save.'; }
    }
    form.addEventListener('change', updateFields);
    function openEditor(date, start = '09:00', end = '17:00', allDay = true, available = true) {
        field('StartDate').value = date; field('EndDate').value = date;
        field('StartTime').value = start; field('EndTime').value = end;
        checkbox('AllDay').checked = allDay; checkbox('ApplyRange').checked = false; checkbox('RepeatYearly').checked = false;
        field('Reason').value = ''; choice(available);
        editor.querySelector('h2').textContent = new Intl.DateTimeFormat('en', { weekday: 'short', day: 'numeric', month: 'short', timeZone: 'UTC' }).format(new Date(date + 'T12:00:00Z'));
        updateFields(); if (dialog) dialog.showModal(); else editor.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }
    page.querySelector('[data-block-dates]').addEventListener('click', () => { choice(false); checkbox('AllDay').checked = true; checkbox('ApplyRange').checked = true; updateFields(); if (dialog && !dialog.open) dialog.showModal(); else editor.scrollIntoView({ behavior: 'smooth', block: 'center' }); field('Reason').focus(); });
    page.querySelectorAll('[data-edit-day]').forEach(link => link.addEventListener('click', event => { event.preventDefault(); openEditor(link.dataset.editDay); }));
    const time = value => `${String(Math.floor(value)).padStart(2, '0')}:${value % 1 ? '30' : '00'}`;
    page.querySelectorAll('[data-week-day]').forEach(column => {
        let selection, start, pointer;
        const hour = y => Math.min(21.5, Math.max(8, Math.floor(((y - column.getBoundingClientRect().top) / column.getBoundingClientRect().height * 14 + 8) * 2) / 2));
        column.addEventListener('pointerdown', event => {
            if (event.button !== 0 || event.target.closest('a') || event.pointerType === 'touch') return;
            start = hour(event.clientY); pointer = event.pointerId; column.setPointerCapture(pointer);
            selection = document.createElement('div'); selection.className = 'week-drag-selection'; column.append(selection);
            selection.style.top = `${(start - 8) / 14 * 100}%`; selection.style.height = `${.5 / 14 * 100}%`;
        });
        column.addEventListener('pointermove', event => {
            if (!selection) return;
            const current = hour(event.clientY); selection.style.top = `${(Math.min(current, start) - 8) / 14 * 100}%`; selection.style.height = `${(Math.abs(current - start) + .5) / 14 * 100}%`;
        });
        column.addEventListener('pointerup', event => {
            if (!selection || event.pointerId !== pointer) return;
            const current = hour(event.clientY); selection.remove(); selection = null;
            openEditor(column.dataset.weekDay, time(Math.min(current, start)), time(Math.max(current, start) + .5), false);
        });
        column.addEventListener('pointercancel', () => { selection?.remove(); selection = null; });
        column.addEventListener('keydown', event => { if (event.target === column && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); openEditor(column.dataset.weekDay, '09:00', '10:00', false); } });
        column.addEventListener('click', event => { if (event.pointerType === 'touch' && !event.target.closest('a')) openEditor(column.dataset.weekDay); });
    });
    updateFields();
})();
