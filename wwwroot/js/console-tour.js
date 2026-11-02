// Show me around (Components/Shared/ConsoleTour.razor): where a stop's part of the console is, by
// its data-tour mark, in the window's coordinates, and a call back when the window is resized or
// scrolled so the outline follows it. Null when the part is not on the page or not shown (the
// column on a phone, the Agent without Observr): the tour skips that stop.
let handler = null;

export function measure(target) {
    const el = document.querySelector(`[data-tour="${target}"]`);
    if (!el) return null;
    const r = el.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) return null;
    if (r.top < 0 || r.bottom > window.innerHeight) el.scrollIntoView({ block: 'nearest' });
    const s = el.getBoundingClientRect();
    return { x: s.left, y: s.top, w: s.width, h: s.height, vw: window.innerWidth, vh: window.innerHeight };
}

export function follow(dotnet) {
    stop();
    let queued = false;
    handler = () => {
        if (queued) return;
        queued = true;
        requestAnimationFrame(() => { queued = false; dotnet.invokeMethodAsync('Remeasure'); });
    };
    window.addEventListener('resize', handler);
    window.addEventListener('scroll', handler, true);
}

export function stop() {
    if (!handler) return;
    window.removeEventListener('resize', handler);
    window.removeEventListener('scroll', handler, true);
    handler = null;
}
