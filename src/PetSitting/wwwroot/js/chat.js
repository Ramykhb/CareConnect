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
    const markRead = () => { if (document.visibilityState === 'visible') connection.invoke('MarkRead', bookingId).catch(() => {}); };
    connection.on('message.received', m => { if (m.bookingId !== bookingId) return; render(m); scroll(); if (m.senderId !== me) markRead(); });
    connection.on('messages.read', messages => messages.forEach(render));
    connection.onreconnected(() => connection.invoke('Join', bookingId).then(markRead));
    document.addEventListener('visibilitychange', markRead);

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

    connection.start().then(() => connection.invoke('Join', bookingId)).then(markRead)
        .catch(() => { status.textContent = 'Live chat is offline; messages still send.'; });
    scroll();
})();
