// ChatPanel's browser half: the Latest button. The panel is marked data-away while its thread is
// scrolled more than a screenful's edge up from the newest turn, which shows the button; pressing
// it puts the thread at its end at once (no motion: systm moves only while waiting). The thread
// growing below a reader who scrolled up shows it too, so they know there is more. The React
// ChatPanel has the same.

const AWAY = 80;   // px from the end before the thread counts as scrolled up
const watched = new Map();   // thread -> its teardown

export function watch(thread) {
    if (!thread || watched.has(thread)) return;
    const panel = thread.closest('.indx-chat');
    const button = panel?.querySelector('[data-chat-jump]');
    if (!panel) return;
    const update = () => {
        const away = thread.scrollHeight - thread.scrollTop - thread.clientHeight > AWAY;
        panel.toggleAttribute('data-away', away);
    };
    const jump = () => { thread.scrollTop = thread.scrollHeight; update(); };
    thread.addEventListener('scroll', update, { passive: true });
    button?.addEventListener('click', jump);
    const observer = new MutationObserver(update);
    observer.observe(thread, { childList: true, subtree: true, characterData: true });
    const resize = new ResizeObserver(update);
    resize.observe(thread);
    update();
    watched.set(thread, () => {
        thread.removeEventListener('scroll', update);
        button?.removeEventListener('click', jump);
        observer.disconnect();
        resize.disconnect();
    });
}

export function unwatch(thread) {
    watched.get(thread)?.();
    watched.delete(thread);
}
