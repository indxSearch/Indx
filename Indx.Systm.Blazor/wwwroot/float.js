// Placing a floating panel by the element it belongs to: Popover's panel by its trigger, a
// Tooltip's bubble by its control. The panel is in the browser's top layer (popover="manual",
// shown with showPopover), so nothing it sits in can clip it, no transform moves it and no
// z-index covers it, and position: fixed means the window. It takes the side asked and flips to
// the other when that side has no room, and is kept inside the window.

const MARGIN = 8;   // the panel never comes closer than this to the window's edge

/** Places panel by anchor. side: top, bottom, left, right; align: start, center, end. Sets
 *  data-side to the side taken and data-placed, and --arrow to where the anchor's middle falls
 *  along the panel's edge, for an arrow that points at it after the panel was moved to fit. */
export function place(anchor, panel, side, align, offset) {
    const t = anchor.getBoundingClientRect();
    const w = panel.offsetWidth, h = panel.offsetHeight;
    const vw = window.innerWidth, vh = window.innerHeight;
    const vertical = side === 'top' || side === 'bottom';
    // The side, flipped when it has no room and the other side has.
    let s = side;
    if (s === 'bottom' && t.bottom + offset + h > vh - MARGIN && t.top - offset - h >= MARGIN) s = 'top';
    else if (s === 'top' && t.top - offset - h < MARGIN && t.bottom + offset + h <= vh - MARGIN) s = 'bottom';
    else if (s === 'right' && t.right + offset + w > vw - MARGIN && t.left - offset - w >= MARGIN) s = 'left';
    else if (s === 'left' && t.left - offset - w < MARGIN && t.right + offset + w <= vw - MARGIN) s = 'right';

    let x, y;
    if (vertical) {
        y = s === 'bottom' ? t.bottom + offset : t.top - offset - h;
        x = align === 'start' ? t.left : align === 'center' ? t.left + (t.width - w) / 2 : t.right - w;
    } else {
        x = s === 'right' ? t.right + offset : t.left - offset - w;
        y = align === 'start' ? t.top : align === 'center' ? t.top + (t.height - h) / 2 : t.bottom - h;
    }
    x = Math.round(Math.max(MARGIN, Math.min(x, vw - MARGIN - w)));
    y = Math.round(Math.max(MARGIN, Math.min(y, vh - MARGIN - h)));
    panel.style.left = `${x}px`;
    panel.style.top = `${y}px`;
    const arrow = vertical ? t.left + t.width / 2 - x : t.top + t.height / 2 - y;
    panel.style.setProperty('--arrow', `${Math.round(Math.max(8, Math.min(arrow, (vertical ? w : h) - 8)))}px`);
    panel.dataset.side = s;
    panel.dataset.placed = '';
}

/** Into the top layer, where a browser has one (every current one does). */
export function raise(panel) {
    if (panel.isConnected && panel.showPopover && !panel.matches(':popover-open')) panel.showPopover();
}

/** Out of it. A panel Blazor already removed has left the top layer with the page. */
export function lower(panel) {
    if (panel?.isConnected && panel.hidePopover && panel.matches(':popover-open')) panel.hidePopover();
}
