// Popover's browser half: raises the panel into the top layer, places it by its trigger
// (float.js) and dismisses it. Nothing it sits in can clip it, and it takes the asked side and
// flips to the other when that side has no room. The React Popover gets the same from Radix.

import { place as placePanel, raise, lower } from './float.js';

const open = new Map();   // content element -> its listeners, while open

export function show(anchor, content, side, align, offset, role, dotnet) {
    if (!anchor || !content) return;
    hide(content);
    const trigger = anchor.querySelector('.indx-popover-trigger');
    const place = () => placePanel(trigger, content, side, align, offset);
    raise(content);
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
    lower(content);
    open.delete(content);
}

// Closed by the component (a choice made, a navigation): the listeners go with the panel.
export function closed(anchor) {
    for (const [content, l] of open) if (anchor?.contains(l.trigger)) hide(content);
    const trigger = anchor?.querySelector('.indx-popover-trigger');
    if (trigger) setExpanded(trigger, false, null);
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
