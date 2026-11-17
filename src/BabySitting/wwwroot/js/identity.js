document.addEventListener('click', event => {
    if (!(event.target instanceof Element)) return;
    const close = event.target.closest('.identity-content [data-bs-dismiss="alert"]');
    if (close) close.closest('.alert')?.remove();
});
