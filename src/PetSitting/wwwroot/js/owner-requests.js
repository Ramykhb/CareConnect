(() => {
    'use strict';
    const form = document.getElementById('request-wizard');
    if (form) {
        const status = form.querySelector('[data-draft-status]');
        const field = name => form.elements.namedItem(`Form.${name}`);
        const version = form.elements.namedItem('version');
        let dirty = false, revision = 0, timer, pending, navigating = false, conflicted = false;
        const summary = (name, text, complete) => {
            const target = form.querySelector(`[data-summary="${name}"]`);
            if (!target) return;
            target.textContent = text || '—';
            target.closest('div').querySelector('.summary-check').classList.toggle('done', complete);
        };
        const dateValue = value => value ? new Date(`${value}T12:00:00`) : null;
        const iso = date => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
        const shortDate = value => dateValue(value)?.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' }) || '';
        const update = () => {
            const pets = [...form.querySelectorAll('[data-pet-name]:checked')].map(input => input.dataset.petName);
            summary('pets', pets.join(', '), pets.length > 0);
            const service = form.querySelector('[name="Form.ServiceType"]:checked');
            const names = { PetSitting: 'Overnight care', DogWalking: 'Dog walking', Boarding: 'Boarding', DropInVisit: 'Drop-in visits' };
            summary('service', names[service?.value], !!service);
            const start = field('StartDate').value, end = field('EndDate').value;
            summary('when', start && end ? `${shortDate(start)} – ${shortDate(end)}` : '', !!start && !!end);
            summary('where', field('City').value.trim(), !!field('City').value.trim());
            const low = field('BudgetMin').value, high = field('BudgetMax').value;
            const budget = low && high ? `$${low} – $${high}` : '';
            summary('budget', budget, !!low && !!high && Number(high) >= Number(low));
            form.querySelector('[data-budget-total]').textContent = budget || 'In USD';
            const from = field('StartTime').value, to = field('EndTime').value;
            const total = form.querySelector('[data-date-total]');
            if (start && end && from && to) {
                const hours = (new Date(`${end}T${to}`) - new Date(`${start}T${from}`)) / 3600000;
                total.textContent = hours > 0 ? `${shortDate(start)} ${from} → ${shortDate(end)} ${to} · Beirut time` : 'The end must be after the start.';
            } else total.textContent = 'Choose your dates and times to see the total duration.';
            drawCalendar();
        };
        const save = async () => {
            if (pending) { await pending; if (dirty && !conflicted) return save(); return; }
            if (!dirty || conflicted) return;
            const savedRevision = revision;
            const data = new FormData(form);
            data.set('command', 'autosave');
            status.textContent = 'Draft · saving…';
            pending = (async () => {
                try {
                    const response = await fetch(form.dataset.autosaveUrl, { method: 'POST', body: data, headers: { 'X-Requested-With': 'XMLHttpRequest' } });
                    const result = await response.json();
                    if (!response.ok) {
                        if (response.status === 409) conflicted = true;
                        throw new Error(result.message || 'Draft not saved. Check your entries and try again.');
                    }
                    if (result.submittedUrl) { navigating = true; location.assign(result.submittedUrl); return; }
                    version.value = result.version;
                    dirty = revision !== savedRevision;
                    status.textContent = dirty ? 'Draft · changes not saved' : 'Draft · auto-saved';
                    const url = new URL(location.href);
                    url.searchParams.set('draftId', result.draftId);
                    url.searchParams.delete('fresh');
                    history.replaceState(null, '', url);
                } catch (error) {
                    status.textContent = error.message || 'Draft not saved. Check your connection.';
                }
            })();
            await pending;
            pending = null;
        };
        const changed = event => {
            dirty = true;
            revision++;
            if (event.target?.name !== 'Form.CareConfirmed') form.querySelector('input[type=checkbox][name="Form.CareConfirmed"]').checked = false;
            status.textContent = 'Draft · changes not saved';
            update();
            clearTimeout(timer);
            timer = setTimeout(save, 1400);
        };
        form.addEventListener('input', changed);
        form.addEventListener('change', event => {
            if (event.target.matches('select, input[type=radio], input[type=checkbox], input[type=date], input[type=time]')) changed(event);
        });
        form.addEventListener('submit', async event => {
            if (navigating) return;
            event.preventDefault();
            clearTimeout(timer);
            const button = event.submitter || form.querySelector('[value="next"], [value="submit"]');
            if (pending) await pending;
            // The regular POST saves every current field and runs step validation.
            // Keep the updated version from any completed autosave.
            navigating = true;
            form.requestSubmit(button);
        });
        window.addEventListener('beforeunload', event => { if (dirty && !navigating) { event.preventDefault(); event.returnValue = ''; } });

        const calendar = form.querySelector('[data-calendar]');
        calendar.hidden = false;
        const initial = dateValue(field('StartDate').value || field('StartDate').min);
        let month = new Date(initial.getFullYear(), initial.getMonth(), 1);
        let choosingEnd = !!field('StartDate').value && !field('EndDate').value;
        function drawCalendar() {
            const grid = calendar.querySelector('[data-calendar-days]');
            calendar.querySelector('[data-calendar-title]').textContent = month.toLocaleDateString('en-GB', { month: 'long', year: 'numeric' });
            grid.replaceChildren();
            const offset = (month.getDay() + 6) % 7;
            for (let i = 0; i < offset; i++) grid.append(document.createElement('span'));
            const days = new Date(month.getFullYear(), month.getMonth() + 1, 0).getDate();
            for (let day = 1; day <= days; day++) {
                const date = new Date(month.getFullYear(), month.getMonth(), day);
                const value = iso(date), button = document.createElement('button');
                button.type = 'button'; button.textContent = day;
                button.setAttribute('aria-label', date.toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }));
                button.disabled = value < field('StartDate').min;
                const start = field('StartDate').value, end = field('EndDate').value;
                button.classList.toggle('selected', value === start || value === end);
                button.classList.toggle('in-range', !!start && !!end && value > start && value < end);
                button.setAttribute('aria-pressed', String(value === start || value === end));
                button.addEventListener('click', () => {
                    if (!choosingEnd || value < field('StartDate').value) {
                        field('StartDate').value = value; field('EndDate').value = ''; choosingEnd = true;
                    } else { field('EndDate').value = value; choosingEnd = false; }
                    changed({ target: field('StartDate') });
                    calendar.querySelector('[data-calendar-hint]').textContent = choosingEnd ? 'Now select an end date.' : 'Dates selected. Choose the care times.';
                });
                grid.append(button);
            }
        }
        calendar.querySelectorAll('[data-month]').forEach(button => button.addEventListener('click', () => { month = new Date(month.getFullYear(), month.getMonth() + Number(button.dataset.month), 1); drawCalendar(); }));
        form.querySelectorAll('[data-duration]').forEach(button => button.addEventListener('click', () => {
            if (!field('StartDate').value) field('StartDate').value = field('StartDate').min;
            const end = dateValue(field('StartDate').value); end.setDate(end.getDate() + Number(button.dataset.duration));
            field('EndDate').value = iso(end); choosingEnd = false;
            changed({ target: field('EndDate') });
        }));
        update();
    }
    const statusPanel = document.querySelector('[data-request-status]');
    if (statusPanel) {
        let failures = 0;
        const refresh = async () => {
            if (!document.hidden) {
                try {
                    const response = await fetch(statusPanel.dataset.requestStatus, { headers: { 'X-Requested-With': 'XMLHttpRequest' } });
                    if (response.redirected || !response.ok) throw new Error();
                    const html = await response.text();
                    if (html !== statusPanel.innerHTML) statusPanel.innerHTML = html;
                    failures = 0;
                } catch {
                    failures++;
                    const label = statusPanel.querySelector('[data-sync-label]');
                    if (label) label.textContent = 'Reconnecting…';
                }
            }
            setTimeout(refresh, Math.min(60000, 15000 * (failures + 1)));
        };
        setTimeout(refresh, 15000);
    }
})();
