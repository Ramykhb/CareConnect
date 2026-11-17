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
