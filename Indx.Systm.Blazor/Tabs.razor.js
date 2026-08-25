// Scroll-aware fade for the scrollable Tabs variant. Toggles data-overflow-start / data-overflow-end
// on the tab list so the CSS mask fades only the edge that actually has content scrolled off it
// (no left fade at rest). Pure DOM — no callbacks into .NET.

export function init(el) {
    if (!el) return;
    const update = () => {
        el.toggleAttribute('data-overflow-start', el.scrollLeft > 1);
        el.toggleAttribute('data-overflow-end', Math.ceil(el.scrollLeft + el.clientWidth) < el.scrollWidth - 1);
    };
    el._indxTabsUpdate = update;
    el.addEventListener('scroll', update, { passive: true });
    const ro = new ResizeObserver(update);
    el._indxTabsRO = ro;
    ro.observe(el);
    update();
}

export function refresh(el) {
    if (el && el._indxTabsUpdate) el._indxTabsUpdate();
}

export function dispose(el) {
    if (!el) return;
    if (el._indxTabsUpdate) el.removeEventListener('scroll', el._indxTabsUpdate);
    if (el._indxTabsRO) el._indxTabsRO.disconnect();
    delete el._indxTabsUpdate;
    delete el._indxTabsRO;
}
