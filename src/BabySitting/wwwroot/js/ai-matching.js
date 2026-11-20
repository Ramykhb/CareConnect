(() => {
    'use strict';
    const form = document.getElementById('request-wizard');
    const button = form?.querySelector('[data-ai-match]');
    const results = form?.querySelector('[data-sitter-matches]');
    const status = form?.querySelector('[data-match-status]');
    if (!button || !results || !status) return;
    let pending;
    let changed = false;

    form.addEventListener('input', event => {
        if (!event.target.name?.startsWith('Form.') || event.target.name === 'Form.PreferredSitterId') return;
        changed = true;
        pending?.abort();
        status.textContent = 'Details changed. Match again to update recommendations.';
    });

    // Capture first so the draft navigation handler does not treat matching as leaving the page.
    form.addEventListener('submit', async event => {
        if (event.submitter !== button) { pending?.abort(); return; }
        event.preventDefault();
        event.stopImmediatePropagation();
        if (pending) return;
        const controller = new AbortController();
        pending = controller;
        changed = false;
        button.disabled = true;
        button.textContent = 'Matching…';
        results.setAttribute('aria-busy', 'true');
        status.textContent = 'Comparing available sitters with your current request…';
        const timeout = setTimeout(() => controller.abort(), 35000);
        try {
            const response = await fetch(button.formAction, {
                method: 'POST', body: new FormData(form), signal: controller.signal,
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            });
            if (response.redirected) throw new Error('Your session expired. Save your draft and sign in again.');
            if (response.status === 429) throw new Error('Too many matching requests. Wait a minute and try again.');
            if (!response.ok) throw new Error('Matching could not finish. Your current sitter list is still available. Try again.');
            const html = await response.text();
            if (controller.signal.aborted) return;
            // Keep the customer's selection, including changes made while Groq was running.
            const selected = form.querySelector('[name="Form.PreferredSitterId"]:checked')?.value || '';
            results.innerHTML = html;
            const options = [...results.querySelectorAll('[name="Form.PreferredSitterId"]')];
            const choice = options.find(input => input.value === selected) || options.find(input => input.value === '');
            if (choice) choice.checked = true;
            if (selected && choice?.value !== selected) {
                status.textContent = 'Your selected sitter is no longer available. Choose another sitter.';
                choice?.dispatchEvent(new Event('input', { bubbles: true }));
            } else status.textContent = 'Recommendations updated. Choose a sitter or keep no preference.';
        } catch (error) {
            if (!changed) status.textContent = error.name === 'AbortError'
                ? 'Matching timed out. Your current sitter list is still available. Try again.'
                : error.message;
        } finally {
            clearTimeout(timeout);
            pending = null;
            button.disabled = false;
            button.textContent = 'Match with AI';
            results.removeAttribute('aria-busy');
        }
    }, true);
})();
