// Popover's browser half: places the panel by its trigger and dismisses it. The panel is fixed to
// the viewport, so no scrolling or clipping parent cuts it off; it takes the asked side and flips
// to the other when that side has no room, and is kept inside the window. The React Popover gets
// the same from Radix.

const MARGIN = 8;   // the panel never comes closer than this to the window's edge
const open = new Map();   // content element -> its listeners, while open

export function show(anchor, content, side, align, offset, role, dotnet) {
    if (!anchor || !content) return;
    hide(content);
    const trigger = anchor.querySelector('.indx-popover-trigger');
    const place = () => position(trigger, content, side, align, offset);
    place();

    const dismiss = (refocus) => {
        dotnet.invokeMethodAsync('Dismiss').catch(() => { });
        if (refocus) focusTrigger(trigger);
    };
    // A press or focus outside the panel and its trigger closes it; the trigger's own click toggles.
    const outside = (e) => {
        if (content.contains(e.target) || trigger.contains(e.target)) return;
        dismiss(false);
    };
    const key = (e) => {
        if (e.key === 'Escape') {
            // A SelectField or DatePicker open inside the panel, with the focus in it, takes the
            // first Escape for itself; its own handler closes its list.
            if (e.target !== content && content.querySelector('[aria-expanded="true"]')) return;
            e.preventDefault(); dismiss(true); return;
        }
        if (content.getAttribute('role') === 'menu' && (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Home' || e.key === 'End')) {
            const items = [...content.querySelectorAll('button:not(:disabled), a[href]')];
            if (!items.length) return;
            const i = items.indexOf(document.activeElement);
            const to = e.key === 'Home' ? 0 : e.key === 'End' ? items.length - 1
                : e.key === 'ArrowDown' ? (i + 1) % items.length : (i - 1 + items.length) % items.length;
            items[to].focus();
            e.preventDefault();
        }
    };
    const listeners = { outside, key, place };
    document.addEventListener('pointerdown', outside, true);
    document.addEventListener('focusin', outside, true);
    content.addEventListener('keydown', key);
    trigger.addEventListener('keydown', key);
    window.addEventListener('resize', place);
    window.addEventListener('scroll', place, true);
    listeners.trigger = trigger;
    open.set(content, listeners);

    setExpanded(trigger, true, role);
    // Into the panel: its first control, or the panel itself.
    const first = content.querySelector('button:not(:disabled), a[href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
    (first ?? content).focus({ preventScroll: true });
}

export function hide(content) {
    const l = content && open.get(content);
    if (!l) return;
    document.removeEventListener('pointerdown', l.outside, true);
    document.removeEventListener('focusin', l.outside, true);
    content.removeEventListener('keydown', l.key);
    l.trigger.removeEventListener('keydown', l.key);
    window.removeEventListener('resize', l.place);
    window.removeEventListener('scroll', l.place, true);
    setExpanded(l.trigger, false, null);
    open.delete(content);
}

// Closed by the component (a choice made, a navigation): the listeners go with the panel.
export function closed(anchor) {
    for (const [content, l] of open) if (anchor?.contains(l.trigger)) hide(content);
    const trigger = anchor?.querySelector('.indx-popover-trigger');
    if (trigger) setExpanded(trigger, false, null);
}

function position(trigger, content, side, align, offset) {
    const t = trigger.getBoundingClientRect();
    const w = content.offsetWidth, h = content.offsetHeight;
    const vw = window.innerWidth, vh = window.innerHeight;
    const vertical = side === 'top' || side === 'bottom';
    // The side, flipped when it has no room and the other side has more.
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
    x = Math.max(MARGIN, Math.min(x, vw - MARGIN - w));
    y = Math.max(MARGIN, Math.min(y, vh - MARGIN - h));
    content.style.left = `${Math.round(x)}px`;
    content.style.top = `${Math.round(y)}px`;
    // An ancestor with a transform, filter or backdrop-filter (the console's header has one) makes
    // a fixed element place itself against that ancestor instead of the window: measure where it
    // landed and move it by the difference.
    const r = content.getBoundingClientRect();
    if (Math.abs(r.left - x) > 0.5 || Math.abs(r.top - y) > 0.5) {
        content.style.left = `${Math.round(2 * x - r.left)}px`;
        content.style.top = `${Math.round(2 * y - r.top)}px`;
    }
    content.dataset.side = s;
    content.dataset.placed = '';
}

function focusTrigger(trigger) {
    trigger?.querySelector('button, a[href], [tabindex]')?.focus();
}

// aria-expanded and aria-haspopup on the trigger's own control, as Radix sets them on its child.
function setExpanded(trigger, value, role) {
    const control = trigger?.querySelector('button, a[href], [role="button"]');
    if (!control) return;
    control.setAttribute('aria-expanded', value ? 'true' : 'false');
    if (role) control.setAttribute('aria-haspopup', role === 'menu' ? 'menu' : 'dialog');
}
