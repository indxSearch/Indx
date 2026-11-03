// Indx.Systm.Blazor's JS initializer: Blazor imports a library's <name>.lib.module.js by itself
// and calls afterWebStarted (a Blazor Web App) or afterStarted (blazor.server.js) once. It holds
// what is better done once for the page than once per component, which today is the Tooltip: a
// few listeners on the document serve every tooltip there is or will be, so a table of hundreds
// costs nothing until one is hovered, and a page Blazor patches needs no wiring of its own.

import { place, raise, lower } from './float.js';

const OFFSET = 7;   // the gap to the control, room for the arrow
let installed = false;
let shown = null;   // { wrapper, tip } while a tooltip is up
let seq = 0;

export function afterWebStarted() { install(); }
export function afterStarted() { install(); }

function install() {
    if (installed) return;
    installed = true;

    // A mouse or pen shows it while over the control. A touch has no hover: a tap shows it and
    // the next tap anywhere else takes it away.
    document.addEventListener('pointerover', e => {
        if (e.pointerType === 'touch') return;
        const w = wrapperOf(e.target);
        if (w && !(shown && shown.wrapper === w)) show(w);
    }, true);
    document.addEventListener('pointerout', e => {
        if (e.pointerType === 'touch' || !shown) return;
        if (wrapperOf(e.target) === shown.wrapper && !shown.wrapper.contains(e.relatedTarget)) hide();
    }, true);
    document.addEventListener('pointerdown', e => {
        const w = wrapperOf(e.target);
        if (e.pointerType === 'touch' && w) { if (!(shown && shown.wrapper === w)) show(w); return; }
        // A press closes it, as Radix does: what was pressed is about to change.
        if (shown) hide();
    }, true);

    // The keyboard shows it on focus. :focus-visible, so a click, which also focuses its button,
    // does not leave it up until a press elsewhere.
    document.addEventListener('focusin', e => {
        const w = wrapperOf(e.target);
        if (w && e.target.matches(':focus-visible')) show(w);
    }, true);
    document.addEventListener('focusout', e => {
        if (shown && wrapperOf(e.target) === shown.wrapper && !shown.wrapper.contains(e.relatedTarget)) hide();
    }, true);
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape' && shown) hide();
    }, true);

    const follow = () => { if (shown) at(shown); };
    window.addEventListener('scroll', follow, true);
    window.addEventListener('resize', follow);
}

/** The Tooltip a node is in: its wrapper, holding the control and the bubble. */
function wrapperOf(node) {
    return node instanceof Element ? node.closest('.indx-tooltip-wrapper') : null;
}

function show(wrapper) {
    const tip = wrapper.querySelector(':scope > .indx-tooltip');
    if (!tip) return;
    if (shown) hide();
    describe(wrapper, tip);
    shown = { wrapper, tip };
    raise(tip);
    at(shown);
}

function hide() {
    if (!shown) return;
    lower(shown.tip);
    shown = null;
}

function at({ wrapper, tip }) {
    if (!wrapper.isConnected) { shown = null; return; }   // Blazor removed it while it was up
    place(wrapper, tip, wrapper.dataset.side || 'top', 'center', OFFSET);
}

/** Points the control at its bubble, so a screen reader reads the hint with it. */
function describe(wrapper, tip) {
    tip.id ||= `indx-tip-${++seq}`;
    const control = wrapper.querySelector(':scope > button, :scope > a[href], :scope > input, :scope > [tabindex], button, a[href], [tabindex]')
        ?? wrapper.firstElementChild;
    if (!control || control === tip) return;
    const ids = (control.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);
    if (!ids.includes(tip.id)) control.setAttribute('aria-describedby', [...ids, tip.id].join(' '));
}
