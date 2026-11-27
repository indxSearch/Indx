// Scroll-aware fade for the scrollable Tabs variant. Sets --tabs-fade-start / --tabs-fade-end on the
// tab list to the amount of content scrolled off each edge, capped, so the CSS mask grows with
// the scroll distance and there is nothing at rest. Pure DOM — no callbacks into .NET.

export function init(el) {
    if (!el) return;
    const keydown = event => {
        if (event.target.matches('[role="tab"]') && ['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) event.preventDefault();
    };
    el._indxTabsKeydown = keydown;
    el.addEventListener('keydown', keydown);
    const update = () => {
        // With bleed the fade is fixed in the gutter and needs no measuring.
        if (el.classList.contains('indx-tabs-bleed')) {
            el.style.removeProperty('--tabs-fade-start');
            el.style.removeProperty('--tabs-fade-end');
            return;
        }
        const max = 32;
        el.style.setProperty('--tabs-fade-start', Math.min(el.scrollLeft, max) + 'px');
        el.style.setProperty('--tabs-fade-end', Math.min(Math.max(el.scrollWidth - el.clientWidth - el.scrollLeft, 0), max) + 'px');
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

// Scrolls the list sideways just far enough to show a tab, clear of the edge fade. Done by hand
// because scrollIntoView also scrolls the page, which a tab bar has no business doing.
export function reveal(el, id) {
    const tab = document.getElementById(id);
    if (!el || !tab) return;
    // Keep the tab clear of the fade: the bleed padding when there is one, else the sliding fade.
    const fade = parseFloat(getComputedStyle(el).paddingInlineStart) || 32;
    const l = el.getBoundingClientRect();
    const t = tab.getBoundingClientRect();
    if (t.left < l.left + fade) el.scrollLeft -= l.left + fade - t.left;
    else if (t.right > l.right - fade) el.scrollLeft += t.right - (l.right - fade);
}

export function focusTab(id, scrollIntoView) {
    const tab = document.getElementById(id);
    if (!tab) return;
    tab.focus({ preventScroll: true });
    if (scrollIntoView) reveal(tab.parentElement, id);
}

export function dispose(el) {
    if (!el) return;
    if (el._indxTabsKeydown) el.removeEventListener('keydown', el._indxTabsKeydown);
    delete el._indxTabsKeydown;
    if (el._indxTabsUpdate) el.removeEventListener('scroll', el._indxTabsUpdate);
    if (el._indxTabsRO) el._indxTabsRO.disconnect();
    delete el._indxTabsUpdate;
    delete el._indxTabsRO;
}
