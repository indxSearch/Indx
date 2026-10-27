// Sheet's browser half: while it is open the focus stays inside (Tab and Shift+Tab wrap), Escape
// closes it, and the page under it does not scroll; on closing the focus goes back to what had it
// before. The React Sheet gets the same from Radix's dialog.

const open = new Map();   // dotnet reference -> its teardown

const FOCUSABLE = 'a[href], button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])';

export function opened(panel, dotnet) {
    if (!panel) return;
    closed(dotnet, false);
    const before = document.activeElement;
    const html = document.documentElement;
    const overflow = html.style.overflow;
    html.style.overflow = 'hidden';
    const onKey = (e) => {
        if (e.key === 'Escape') {
            // An open list inside it (a SelectField, a Combobox) takes the first Escape.
            if (e.target !== panel && panel.querySelector('[aria-expanded="true"]')) return;
            e.preventDefault();
            dotnet.invokeMethodAsync('Dismiss').catch(() => { });
        } else if (e.key === 'Tab') {
            const items = [...panel.querySelectorAll(FOCUSABLE)].filter(el => el.offsetParent !== null);
            if (!items.length) { e.preventDefault(); panel.focus(); return; }
            const first = items[0], last = items[items.length - 1];
            if (e.shiftKey && (document.activeElement === first || document.activeElement === panel)) { e.preventDefault(); last.focus(); }
            else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
        }
    };
    document.addEventListener('keydown', onKey, true);
    // Into the panel: its body's first control, or the panel itself; never the close button first.
    const target = panel.querySelector('.indx-sheet-body')?.querySelector(FOCUSABLE);
    (target ?? panel).focus({ preventScroll: true });
    open.set(dotnet, (refocus) => {
        document.removeEventListener('keydown', onKey, true);
        html.style.overflow = overflow;
        if (refocus && before && document.contains(before)) before.focus({ preventScroll: true });
    });
}

export function closed(dotnet, refocus = true) {
    open.get(dotnet)?.(refocus);
    open.delete(dotnet);
}
