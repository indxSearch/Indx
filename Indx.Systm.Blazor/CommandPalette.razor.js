// CommandPalette's browser half: the shortcut that opens it (Cmd or Ctrl and the key, anywhere on
// the page), and the keys inside it. The highlight moves here, with no round trip: the arrows,
// Home and End move it, the pointer moves it, Enter clicks the highlighted item (its own handler
// does the rest) and Escape closes. The React CommandPalette has the same keys.

const shortcuts = new Map();   // dotnet reference -> its document listener

export function listen(dotnet, key) {
    unlisten(dotnet);
    const onKey = (e) => {
        if ((e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === key) {
            e.preventDefault();
            dotnet.invokeMethodAsync('Toggle').catch(() => { });
        }
    };
    document.addEventListener('keydown', onKey);
    shortcuts.set(dotnet, onKey);
}

export function unlisten(dotnet) {
    const onKey = shortcuts.get(dotnet);
    if (onKey) document.removeEventListener('keydown', onKey);
    shortcuts.delete(dotnet);
}

const wired = new WeakSet();

// The panel was drawn: focus its field, wire its keys once, and put the highlight on the first
// item (the results changed, so the old highlight means nothing).
export function opened(panel, dotnet) {
    if (!panel) return;
    const input = panel.querySelector('input');
    if (!wired.has(panel)) {
        wired.add(panel);
        input?.addEventListener('keydown', (e) => {
            const items = [...panel.querySelectorAll('[data-palette-item]')];
            const i = items.findIndex((el) => el.hasAttribute('data-active'));
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Home' || e.key === 'End') {
                if (!items.length) return;
                e.preventDefault();
                const to = e.key === 'Home' ? 0 : e.key === 'End' ? items.length - 1
                    : e.key === 'ArrowDown' ? Math.min(items.length - 1, i + 1) : Math.max(0, i - 1);
                activate(panel, items[to]);
            } else if (e.key === 'Enter') {
                e.preventDefault();
                (items[i] ?? items[0])?.click();
            } else if (e.key === 'Escape') {
                e.preventDefault();
                dotnet.invokeMethodAsync('Close').catch(() => { });
            }
        });
        panel.addEventListener('pointermove', (e) => {
            const item = e.target.closest('[data-palette-item]');
            if (item && !item.hasAttribute('data-active')) activate(panel, item, false);
        });
    }
    if (document.activeElement !== input) input?.focus();
    reset(panel);
}

// New results: the highlight back on the first.
export function reset(panel) {
    if (!panel) return;
    const first = panel.querySelector('[data-palette-item]');
    panel.querySelectorAll('[data-active]').forEach((el) => el !== first && clear(el));
    if (first) activate(panel, first, false);
}

function activate(panel, item, scroll = true) {
    panel.querySelectorAll('[data-active]').forEach((el) => el !== item && clear(el));
    item.setAttribute('data-active', '');
    item.setAttribute('aria-selected', 'true');
    const input = panel.querySelector('input');
    if (input && item.id) input.setAttribute('aria-activedescendant', item.id);
    if (scroll) item.scrollIntoView({ block: 'nearest' });
}

function clear(el) {
    el.removeAttribute('data-active');
    el.setAttribute('aria-selected', 'false');
}

// For a trigger that shows the shortcut: Cmd on a Mac, Ctrl elsewhere.
export function isMac() {
    return /Mac|iPhone|iPad|iPod/.test(navigator.platform) || /Mac OS X/.test(navigator.userAgent);
}
