(() => {
    const tabs = [...document.querySelectorAll('.pet-profile-tabs a')];
    if (!tabs.length) return;
    const update = () => {
        const current = tabs.some(tab => tab.hash === location.hash) ? location.hash : '#overview';
        tabs.forEach(tab => {
            const active = tab.hash === current;
            tab.classList.toggle('active', active);
            if (active) tab.setAttribute('aria-current', 'location');
            else tab.removeAttribute('aria-current');
        });
    };
    window.addEventListener('hashchange', update);
    update();
})();
