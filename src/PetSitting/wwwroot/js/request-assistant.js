(() => {
    'use strict';
    const root = document.querySelector('[data-request-assistant]');
    if (!root) return;
    const find = name => root.querySelector(`[data-ra-${name}]`);
    const panel = root.querySelector('.ra-panel');
    const toggle = find('toggle'), input = find('input'), send = find('send'), messages = find('messages');
    const status = find('connection'), error = find('error'), retry = find('retry'), reset = find('reset');
    let connection, snapshot, busy = false, connecting, pendingMessage;
    const showError = text => { error.textContent = text || ''; error.hidden = !text; };
    const online = () => connection?.state === window.signalR?.HubConnectionState.Connected;
    const controls = () => {
        send.disabled = busy || !online() || !snapshot || !snapshot.available || !!snapshot.draftUrl;
        input.disabled = busy || !!snapshot?.draftUrl;
        reset.disabled = busy || !online() || !snapshot;
        find('typing').hidden = !busy;
        messages.setAttribute('aria-busy', String(busy));
    };
    const setConnection = (text, connected) => {
        status.textContent = text;
        status.dataset.connected = String(connected);
        retry.hidden = connected;
        controls();
    };
    function bubble(role, text) {
        const article = document.createElement('article');
        article.className = `ra-message${role === 'user' ? ' ra-message-user' : ''}`;
        const label = document.createElement('small'); label.textContent = role === 'user' ? 'You' : 'CareConnect assistant';
        const body = document.createElement('p'); body.textContent = text;
        article.append(label, body); messages.append(article);
    }
    function render(data) {
        snapshot = data;
        messages.replaceChildren();
        data.messages.forEach(turn => bubble(turn.role, turn.content));
        if (pendingMessage && data.messages.some(turn => turn.clientMessageId === pendingMessage.id)) {
            if (input.value === pendingMessage.text) input.value = '';
            pendingMessage = null;
        }
        const fields = find('fields'); fields.replaceChildren();
        data.fields.forEach(field => {
            const label = document.createElement('dt'); label.textContent = field.name;
            const value = document.createElement('dd'); value.textContent = field.value;
            fields.append(label, value);
        });
        find('summary').hidden = data.fields.length === 0;
        const draft = find('draft'); draft.hidden = !data.draftUrl;
        // Only accept the application's own draft review path.
        if (data.draftUrl && /^\/CareRequests\/Create\?draftId=[0-9a-f-]{36}$/i.test(data.draftUrl)) find('draft-link').href = data.draftUrl;
        else if (data.draftUrl) draft.hidden = true;
        if (!data.available) showError('The AI assistant is not configured. You can still use Create a request from the menu.');
        controls();
        messages.scrollTop = messages.scrollHeight;
    }
    async function library() {
        if (window.signalR) return;
        await new Promise((resolve, reject) => {
            const script = document.createElement('script'); script.src = root.dataset.signalrUrl;
            script.onload = resolve; script.onerror = () => reject(new Error('The chat connection could not load. Try reconnecting.'));
            document.head.append(script);
        });
    }
    async function sync() {
        render(await connection.invoke('Get'));
        setConnection('Connected · Beirut time', true);
    }
    async function connect() {
        if (connecting) return connecting;
        connecting = (async () => {
            try {
                showError(''); setConnection('Connecting…', false);
                await library();
                if (!connection) {
                    connection = new signalR.HubConnectionBuilder().withUrl(root.dataset.hubUrl)
                        .withAutomaticReconnect([0, 2000, 5000, 10000]).build();
                    connection.onreconnecting(() => setConnection('Reconnecting…', false));
                    connection.onreconnected(() => sync().catch(() => showError('Could not restore chat. Try reconnecting.')));
                    connection.onclose(() => setConnection('Disconnected', false));
                }
                if (connection.state === signalR.HubConnectionState.Disconnected) await connection.start();
                if (online()) await sync();
            } catch {
                setConnection('Could not connect', false);
                showError('Could not open the assistant. Check your connection and sign-in, then reconnect.');
            } finally { connecting = null; controls(); }
        })();
        return connecting;
    }
    function open() {
        panel.hidden = false; toggle.setAttribute('aria-expanded', 'true');
        connect().then(() => { if (!panel.hidden) input.focus(); });
    }
    function close() { panel.hidden = true; toggle.setAttribute('aria-expanded', 'false'); toggle.focus(); }
    toggle.addEventListener('click', () => panel.hidden ? open() : close());
    find('close').addEventListener('click', close);
    root.addEventListener('keydown', event => { if (event.key === 'Escape' && !panel.hidden) close(); });
    retry.addEventListener('click', async () => {
        if (online()) { try { await sync(); showError(''); } catch { showError('Could not restore chat. Please try again.'); } }
        else await connect();
    });
    input.addEventListener('keydown', event => {
        if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) { event.preventDefault(); if (!send.disabled) find('form').requestSubmit(); }
    });
    find('form').addEventListener('submit', async event => {
        event.preventDefault();
        const text = input.value.trim();
        if (!text || send.disabled || text.length > 1500) return;
        if (!pendingMessage || pendingMessage.text !== text || pendingMessage.session !== snapshot.conversationId)
            pendingMessage = { id: crypto.randomUUID(), text, session: snapshot.conversationId };
        const turn = pendingMessage;
        busy = true; controls(); showError(''); bubble('user', text); input.value = '';
        messages.scrollTop = messages.scrollHeight;
        try { render(await connection.invoke('Send', turn.session, turn.id, turn.text)); }
        catch (e) {
            input.value = turn.text;
            // Refresh persisted memory before retrying an uncertain result or a concurrent-tab conflict.
            if (online()) { try { await sync(); } catch { /* reconnect button remains available */ } }
            const description = String(e.message || '');
            showError(description.includes('HubException:') ? description.split('HubException:').pop().trim()
                : 'Your message could not finish. Reconnect if needed, then send it again. Your saved conversation is kept.');
            retry.hidden = false;
        } finally { busy = false; controls(); input.focus(); }
    });
    reset.addEventListener('click', async () => {
        if (busy || !online() || !snapshot) return;
        busy = true; controls(); showError('');
        try { render(await connection.invoke('Reset', snapshot.conversationId)); input.value = ''; pendingMessage = null; }
        catch { showError('Could not start a new chat. Reconnect to load the current conversation.'); retry.hidden = false; }
        finally { busy = false; controls(); input.focus(); }
    });
})();
