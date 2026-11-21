(() => {
    'use strict';
    const card = document.querySelector('[data-care-summary]');
    if (!card) return;
    const form = card.querySelector('[data-cs-form]'), button = card.querySelector('[data-cs-generate]');
    const status = card.querySelector('[data-cs-status]'), result = card.querySelector('[data-cs-result]');
    const scope = form.elements.namedItem('scope'), date = form.elements.namedItem('date');
    let pending;
    const element = (tag, text, className) => {
        const node = document.createElement(tag); if (text !== undefined) node.textContent = text;
        if (className) node.className = className; return node;
    };
    const timestamp = value => value ? new Date(value).toLocaleString('en-GB', { timeZone: 'Asia/Beirut', day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : '';
    function changed() {
        pending?.abort('period-changed');
        date.disabled = scope.value === 'all'; date.required = scope.value === 'day';
        card.querySelector('[data-cs-date-label]').hidden = scope.value === 'all';
        result.hidden = true; result.replaceChildren();
        status.classList.remove('cs-error'); status.textContent = 'Period changed. Generate a summary for these records.';
    }
    scope.addEventListener('change', changed); date.addEventListener('change', changed);
    function render(data) {
        result.replaceChildren(); result.hidden = false;
        result.append(element('p', `${data.date || 'Entire booking'} · Snapshot generated ${timestamp(data.generatedAt)} (Beirut time)`, 'cs-meta'));
        if (data.date) result.append(element('p', `Checklist status reflects this period through ${timestamp(data.statusAsOf || data.generatedAt)} (Beirut time).`, 'cs-footnote'));
        if (data.undatedTasks > 0) result.append(element('p', `${data.undatedTasks} undated checklist item(s) fall outside this daily view. Choose Entire booking to include them.`, 'cs-footnote'));
        const counts = element('div', undefined, 'cs-counts');
        counts.append(element('span', `${data.completedTasks} marked complete`), element('span', `${data.pendingTasks} not marked complete`), element('span', `${data.issues} reported issue(s)`, data.issues ? 'cs-issue-count' : ''));
        result.append(counts);
        const records = element('details', undefined, 'cs-records');
        const sources = Array.isArray(data.sources) ? data.sources : [];
        const known = new Set(sources.map(s => s.key));
        const list = element('ul', undefined, 'cs-highlights');
        for (const sentence of data.sentences || []) {
            const item = element('li'); item.append(element('p', sentence.text));
            const citations = element('div', undefined, 'cs-citations');
            for (const key of sentence.sourceKeys || []) {
                if (!known.has(key) || !/^s\d+$/.test(key)) continue;
                const link = element('a', `Record ${key.slice(1)}`); link.href = `#care-summary-source-${key}`;
                link.addEventListener('click', () => { records.open = true; }); citations.append(link);
            }
            item.append(citations); list.append(item);
        }
        if (list.childElementCount) result.append(list);
        const issues = sources.filter(s => s.isIssue);
        if (issues.length) {
            const box = element('section', undefined, 'cs-issues'); box.append(element('h3', 'Reported issues · original sitter notes'));
            const items = element('ul'); issues.forEach(s => items.append(element('li', `${timestamp(s.recordedAt)} — ${s.text}`)));
            box.append(items); result.append(box);
        }
        const pendingTasks = sources.filter(s => s.kind === 'task' && !s.completedAt);
        if (pendingTasks.length) {
            const box = element('section', undefined, 'cs-pending'); box.append(element('h3', 'Not marked complete'));
            const items = element('ul'); pendingTasks.forEach(s => items.append(element('li', s.text + (s.dueAt ? ` · due ${timestamp(s.dueAt)}` : ' · no due time recorded'))));
            box.append(items); result.append(box);
        }
        if (sources.length) {
            records.append(element('summary', `View recorded care (${sources.length}${data.limited ? ' shown' : ''})`));
            for (const source of sources) {
                const row = element('article', undefined, 'cs-record');
                if (/^s\d+$/.test(source.key)) row.id = `care-summary-source-${source.key}`;
                row.append(element('h4', `${source.key.toUpperCase()} · ${source.kind === 'task' ? source.completedAt ? 'Task marked complete' : 'Task not marked complete' : source.isIssue ? 'Issue reported by sitter' : 'Sitter update'}`), element('p', source.text));
                row.append(element('small', source.kind === 'task'
                    ? [source.dueAt ? `Due: ${timestamp(source.dueAt)}` : 'No due time recorded', source.completedAt ? `Marked complete: ${timestamp(source.completedAt)}` : 'Not marked complete as of this period’s cutoff'].join(' · ')
                    : `Recorded: ${timestamp(source.recordedAt)} · Beirut time`));
                if (source.originalUrl && /^\/(OwnerBookings|ParentBookings)\/Details\/[0-9a-f-]{36}#care-update-[0-9a-f-]{36}$/i.test(source.originalUrl)) {
                    const link = element('a', 'Open original update'); link.href = source.originalUrl; row.append(link);
                }
                records.append(row);
            }
            result.append(records);
        }
        result.append(element('p', 'AI highlights can be incomplete or mistaken. Check the source records for context. An unfinished checkbox does not prove care was missed. Generate again after new sitter updates.', 'cs-footnote'));
    }
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (pending || !form.reportValidity()) return;
        const controller = new AbortController(); pending = controller;
        button.disabled = true; button.textContent = 'Summarizing…';
        result.hidden = true; card.setAttribute('aria-busy', 'true');
        status.classList.remove('cs-error'); status.textContent = 'Reading recorded care tasks and sitter updates…';
        const timeout = setTimeout(() => controller.abort(), 70000);
        try {
            const response = await fetch(form.action, { method: 'POST', body: new FormData(form), signal: controller.signal, headers: { 'X-Requested-With': 'XMLHttpRequest' } });
            if (response.redirected || response.status === 401 || response.status === 403) throw new Error('Please sign in again to view this booking’s care records.');
            if (response.status === 429) throw new Error('Too many summary requests. Wait a minute and try again.');
            if (response.status === 404) throw new Error('This booking is no longer available to your account.');
            const data = await response.json();
            if (!response.ok) throw new Error(data.message || 'Could not generate a summary. Try again.');
            if (controller.signal.aborted) return;
            status.textContent = data.message; render(data);
        } catch (error) {
            if (controller.signal.reason === 'period-changed') return;
            status.classList.add('cs-error');
            status.textContent = error.name === 'AbortError' ? 'The summary timed out. Your original care records are still available. Try again.' : error.message;
        } finally {
            clearTimeout(timeout); pending = null; button.disabled = false; button.textContent = 'Summarize care'; card.removeAttribute('aria-busy');
        }
    });
})();
