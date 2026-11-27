// HistogramRange's browser half: the drag across the bars. While dragging, the bars between the one
// pressed and the one under the pointer are lit here, with no round trip; .NET hears the range once
// the pointer is let go (Picked), and renders the numbers and the bars from it.

const histograms = new Map();   // bars element -> state

function barAt(s, clientX) {
    const bars = s.el.children;
    if (bars.length === 0) return -1;
    const rect = s.el.getBoundingClientRect();
    const i = Math.floor(((clientX - rect.left) / rect.width) * bars.length);
    return Math.max(0, Math.min(bars.length - 1, i));
}

function light(s, a, b) {
    const lo = Math.min(a, b), hi = Math.max(a, b);
    Array.from(s.el.children).forEach((bar, i) => bar.classList.toggle('indx-histrange-bar--in', i >= lo && i <= hi));
}

export function sync(el, dotnet, opts) {
    let s = histograms.get(el);
    if (!s) {
        s = { el, dotnet, opts };
        const onDown = (e) => {
            if (s.opts.disabled || e.button !== 0) return;
            e.preventDefault();
            const anchor = barAt(s, e.clientX);
            if (anchor < 0) return;
            let at = anchor;
            light(s, anchor, at);
            try { el.setPointerCapture(e.pointerId); } catch { }
            const onMove = (m) => {
                const next = barAt(s, m.clientX);
                if (next !== at) { at = next; light(s, anchor, at); }
            };
            const onUp = () => {
                el.removeEventListener('pointermove', onMove);
                el.removeEventListener('pointerup', onUp);
                el.removeEventListener('pointercancel', onUp);
                try { el.releasePointerCapture(e.pointerId); } catch { }
                s.dotnet.invokeMethodAsync('Picked', anchor, at).catch(() => { });
            };
            el.addEventListener('pointermove', onMove);
            el.addEventListener('pointerup', onUp);
            el.addEventListener('pointercancel', onUp);
        };
        el.addEventListener('pointerdown', onDown);
        s.teardown = () => el.removeEventListener('pointerdown', onDown);
        histograms.set(el, s);
    }
    s.dotnet = dotnet;
    s.opts = opts;
}

export function dispose(el) {
    histograms.get(el)?.teardown?.();
    histograms.delete(el);
}
