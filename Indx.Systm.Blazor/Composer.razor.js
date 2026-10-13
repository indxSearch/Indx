// Grows the composer's field with its text up to a cap, as the React Composer does: reset the
// height, then take the scroll height. Runs in the browser on every input, so typing costs no
// round-trip; .NET calls fit when it sets the text itself (cleared after a send).

export function init(el, cap) {
    if (!el) return;
    const fit = () => {
        el.style.height = 'auto';
        el.style.height = Math.min(el.scrollHeight, cap) + 'px';
    };
    el._indxComposerFit = fit;
    el.addEventListener('input', fit);
    fit();
}

export function fit(el) {
    if (el && el._indxComposerFit) el._indxComposerFit();
}

export function dispose(el) {
    if (!el || !el._indxComposerFit) return;
    el.removeEventListener('input', el._indxComposerFit);
    delete el._indxComposerFit;
}
