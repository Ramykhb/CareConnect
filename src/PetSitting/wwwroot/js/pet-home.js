(() => {
    const toggle = document.querySelector('.mobile-menu-toggle');
    const navigation = document.getElementById('mobile-navigation');
    if (!toggle || !navigation) return;
    const close = () => { toggle.setAttribute('aria-expanded', 'false'); navigation.hidden = true; };
    toggle.addEventListener('click', () => {
        const expanded = toggle.getAttribute('aria-expanded') === 'true';
        toggle.setAttribute('aria-expanded', String(!expanded));
        navigation.hidden = expanded;
    });
    navigation.addEventListener('click', event => { if (event.target.closest('a')) close(); });
    document.addEventListener('keydown', event => { if (event.key === 'Escape') { close(); toggle.focus(); } });
    window.matchMedia('(min-width: 961px)').addEventListener('change', event => { if (event.matches) close(); });
})();
