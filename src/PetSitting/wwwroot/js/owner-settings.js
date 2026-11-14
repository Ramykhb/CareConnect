(() => {
    const navigation = document.querySelector('.os-navigation');
    if (!navigation) return;
    const links = [...navigation.querySelectorAll('a[href^="#"]')];
    const select = () => links.forEach(link => {
        const active = link.hash === (location.hash || '#personal-settings');
        link.classList.toggle('active', active);
        if (active) link.setAttribute('aria-current', 'location'); else link.removeAttribute('aria-current');
    });
    window.addEventListener('hashchange', select);
    select();
    const file = document.getElementById('profile-photo');
    file?.addEventListener('change', () => file.setCustomValidity(file.files[0]?.size > 2097152 ? 'Choose a photo up to 2 MB.' : ''));
    const error = document.querySelector('.field-validation-error, .os-errors.validation-summary-errors');
    if (error) {
        const section = error.closest('section');
        if (section?.id) { history.replaceState(null, '', '#' + section.id); select(); }
        error.scrollIntoView({ block: 'center' });
    }
})();
