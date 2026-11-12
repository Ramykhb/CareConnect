(() => {
    const password = document.querySelector('[data-password]');
    const confirmation = document.querySelector('[data-confirm]');
    if (!password || !confirmation) return;
    const bars = [...document.querySelectorAll('.password-meter span')];
    const guidance = document.getElementById('password-guidance');
    const confirmWrap = document.querySelector('[data-confirm-wrap]');
    const update = () => {
        const value = password.value;
        const score = [value.length >= 12, /[a-z]/.test(value) && /[A-Z]/.test(value), /[0-9]/.test(value) && /[^a-zA-Z0-9]/.test(value), new Set(value).size >= 4 && value.length >= 12].filter(Boolean).length;
        bars.forEach((bar, index) => { bar.dataset.filled = String(index < score); });
        guidance.textContent = score === 4 ? 'Strong password' : '12+ characters with uppercase, lowercase, a number and a symbol.';
        confirmWrap.classList.toggle('is-match', confirmation.value.length > 0 && confirmation.value === value);
    };
    password.addEventListener('input', update);
    confirmation.addEventListener('input', update);
    update();
})();
