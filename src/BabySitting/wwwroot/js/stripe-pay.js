(() => {
    'use strict';
    let stripeClient = null;
    let stripeKey = null;

    function getStripe(key) {
        if (!window.Stripe) return null;
        if (!stripeClient || stripeKey !== key) { stripeClient = window.Stripe(key); stripeKey = key; }
        return stripeClient;
    }
    function setError(form, message) {
        const element = form.querySelector('[data-stripe-error]');
        if (element) element.textContent = message || '';
    }
    function setBusy(form, busy) {
        const button = form.querySelector('button[type="submit"]');
        if (!button) return;
        if (busy) {
            if (!button.dataset.label) button.dataset.label = button.innerHTML;
            button.disabled = true;
            button.textContent = 'Processing…';
        } else {
            button.disabled = false;
            if (button.dataset.label) button.innerHTML = button.dataset.label;
        }
    }
    async function post(url, body) {
        const response = await fetch(url, {
            method: 'POST',
            body,
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'fetch' }
        });
        const text = await response.text();
        try { return { ok: response.ok, data: JSON.parse(text) }; }
        catch { return { ok: false, data: { error: 'The payment service returned an unexpected response.' } }; }
    }
    function init(form) {
        if (form.dataset.stripeReady === 'true') return;
        const key = form.dataset.publishableKey;
        const intentUrl = form.dataset.intentUrl;
        const mount = form.querySelector('[data-stripe-card]');
        if (!key || !intentUrl || !mount) return;
        const stripe = getStripe(key);
        if (!stripe) {
            setError(form, 'Card payments could not be loaded. Refresh the page and try again.');
            return;
        }
        if (mount.dataset.stripeMounted === 'true') return;
        form.dataset.stripeReady = 'true';
        mount.dataset.stripeMounted = 'true';
        const card = stripe.elements().create('card', { hidePostalCode: true });
        let complete = false;
        card.mount(mount);
        card.on('change', event => {
            complete = !!event.complete;
            setError(form, event.error ? event.error.message : '');
        });

        form.addEventListener('submit', async event => {
            event.preventDefault();
            if (form.dataset.stripeBusy === 'true') return;
            if (!mount.isConnected) {
                setError(form, 'This payment form was refreshed. Reload the page and enter your card details again.');
                return;
            }
            // `complete` tracks Stripe's change event; the class check covers autofill cases where the event was missed.
            const domComplete = !!mount.querySelector('.StripeElement--complete');
            if (!complete && !domComplete) {
                setError(form, 'Enter your full card number, expiry date and CVC.');
                card.focus();
                return;
            }
            setError(form, '');
            form.dataset.stripeBusy = 'true';
            setBusy(form, true);
            try {
                const intent = await post(intentUrl, new FormData(form));
                if (!intent.ok || !intent.data.clientSecret) {
                    setError(form, intent.data.error || 'The card payment could not be started.');
                    return;
                }
                if (!mount.isConnected) {
                    setError(form, 'This payment form was refreshed. Reload the page and enter your card details again.');
                    return;
                }
                const confirmation = await stripe.confirmCardPayment(intent.data.clientSecret, { payment_method: { card } });
                if (confirmation.error) {
                    setError(form, confirmation.error.message || 'Your card could not be charged.');
                    return;
                }
                if (!confirmation.paymentIntent || confirmation.paymentIntent.status !== 'succeeded') {
                    setError(form, 'The card payment was not completed.');
                    return;
                }
                const body = new FormData(form);
                body.set('paymentIntentId', confirmation.paymentIntent.id);
                const recorded = await post(form.action, body);
                if (!recorded.ok || !recorded.data.ok) {
                    setError(form, recorded.data.message || 'The payment was taken but could not be recorded. Contact support.');
                    return;
                }
                window.location.href = recorded.data.redirect || window.location.href;
            } catch {
                setError(form, 'The payment service could not be reached. Try again in a moment.');
            } finally {
                form.dataset.stripeBusy = 'false';
                setBusy(form, false);
            }
        });
    }
    function initAll() { document.querySelectorAll('form[data-stripe-pay]').forEach(init); }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initAll);
    else initAll();
    // The booking pages refresh themselves over fetch and replace the payment card.
    new MutationObserver(initAll).observe(document.body, { childList: true, subtree: true });
})();
