// Booking chat over SignalR: join the booking's group, send over the socket, show read receipts.
(() => {
    const card = document.querySelector('[data-chat]');
    if (!card || !window.signalR) return;
    const bookingId = card.dataset.chat, me = card.dataset.me;
    const list = card.querySelector('[data-chat-messages]'), empty = card.querySelector('[data-chat-empty]');
    const form = card.querySelector('[data-chat-form]'), input = form.querySelector('textarea'), status = card.querySelector('[data-chat-status]');
    const time = iso => new Date(iso).toLocaleString([], { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
    const scroll = () => { list.scrollTop = list.scrollHeight; };

    const render = m => {
        let li = list.querySelector(`[data-message="${m.id}"]`);
        const mine = m.senderId === me;
        if (!li) {
            li = document.createElement('li');
            li.className = `chat-bubble ${mine ? 'mine' : 'theirs'}`;
            li.dataset.message = m.id;
            const p = document.createElement('p'); p.textContent = m.body;
            const small = document.createElement('small');
            const t = document.createElement('time'); t.dateTime = m.createdAtUtc; t.textContent = time(m.createdAtUtc);
            const receipt = document.createElement('span'); receipt.dataset.expires = '';
            small.append(t, receipt); li.append(p, small); list.append(li);
            empty.hidden = true;
        }
        const receipt = li.querySelector('[data-expires]');
        if (receipt) receipt.textContent = m.expiresAtUtc ? ` · ${mine ? 'Read' : 'Seen'} · disappears ${time(m.expiresAtUtc)}` : (mine ? ' · Sent' : '');
        return li;
    };

    const connection = new signalR.HubConnectionBuilder().withUrl('/hubs/chat')
        .withAutomaticReconnect({ nextRetryDelayInMilliseconds: ctx => Math.min(2000 * 2 ** ctx.previousRetryCount, 60000) }).build();
    const markRead = async () => {
        if (document.visibilityState !== 'visible' || connection.state !== signalR.HubConnectionState.Connected) return;
        // Acknowledge only messages already displayed, including when history is still loading.
        const ids = [...list.querySelectorAll('[data-message]')].map(li => li.dataset.message);
        for (let i = 0; i < ids.length; i += 200) await connection.invoke('MarkRead', bookingId, ids.slice(i, i + 200));
    };
    let syncing = false;
    const sync = async () => {
        if (syncing || connection.state !== signalR.HubConnectionState.Connected) return;
        syncing = true;
        try {
            const messages = await connection.invoke('Sync', bookingId);
            messages.forEach(render); // render merges by ID with live events received during the fetch
            [...list.querySelectorAll('[data-message]')].sort((a, b) =>
                new Date(a.querySelector('time').dateTime) - new Date(b.querySelector('time').dateTime)
                || a.dataset.message.localeCompare(b.dataset.message)).forEach(li => list.append(li));
            await markRead();
            status.textContent = ''; scroll();
        } catch {
            status.textContent = 'Catching up on messages…';
            setTimeout(sync, 2000);
        } finally { syncing = false; }
    };
    connection.on('message.received', m => {
        if (m.bookingId !== bookingId) return;
        render(m); scroll(); if (m.senderId !== me) markRead().catch(() => {});
    });
    connection.on('messages.read', messages => messages.forEach(m => {
        if (list.querySelector(`[data-message="${m.id}"]`)) render(m);
    }));
    connection.onreconnected(sync);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) sync(); });

    form.addEventListener('submit', event => {
        if (connection.state !== signalR.HubConnectionState.Connected) return; // fall back to the normal form post
        event.preventDefault();
        const body = input.value.trim();
        if (!body) return;
        input.disabled = true;
        connection.invoke('Send', bookingId, body)
            .then(() => { input.value = ''; status.textContent = ''; })
            .catch(err => { status.textContent = err?.message?.replace(/^.*HubException: /, '') || 'Message not sent. Try again.'; })
            .finally(() => { input.disabled = false; input.focus(); });
    });
    input.addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); form.requestSubmit(); } });

    connection.start().then(sync)
        .catch(() => { status.textContent = 'Live chat is offline; messages still send.'; });
    scroll();
})();
