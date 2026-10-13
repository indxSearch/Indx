// Grows the composer's field with its text up to a cap, as the React Composer does: reset the
// height, then take the scroll height. Runs in the browser on every input, so typing costs no
// round-trip; .NET calls fit when it sets the text itself (cleared after a send). It refits when
// its width changes too: a field measured while its panel slides open from nothing is narrow, so
// its text wraps on every word and it came out six lines tall with nothing in it.

export function init(el, cap) {
    if (!el) return;
    const fit = () => {
        el.style.height = 'auto';
        el.style.height = Math.min(el.scrollHeight, cap) + 'px';
    };
    el._indxComposerFit = fit;
    el.addEventListener('input', fit);
    let width = el.clientWidth;
    const ro = new ResizeObserver(() => {
        if (el.clientWidth === width) return;   // our own height change: nothing to refit
        width = el.clientWidth;
        fit();
    });
    ro.observe(el);
    el._indxComposerRO = ro;
    fit();
}

export function fit(el) {
    if (el && el._indxComposerFit) el._indxComposerFit();
}

export function dispose(el) {
    if (!el || !el._indxComposerFit) return;
    el.removeEventListener('input', el._indxComposerFit);
    el._indxComposerRO?.disconnect();
    delete el._indxComposerFit;
    delete el._indxComposerRO;
}
