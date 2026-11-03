// Places an open SelectField menu against the window rather than its own field.
//
// The menu is position: absolute inside the field, which any scrolling ancestor clips: a Modal's
// content box is overflow-y: auto, so a select near the bottom of a form opened into a sliver.
// Here the menu is switched to position: fixed at the field's on-screen rectangle, and opens
// upward when there is more room above than below.
//
// A MutationObserver does it, not a call from .NET after render: its callback runs before the
// browser paints, so the menu is never seen at its unplaced position for a round-trip first.

const GAP = 4;
const MARGIN = 8;
const MAX_HEIGHT = 280;

function place(trigger, menu) {
    const r = trigger.getBoundingClientRect();
    const below = window.innerHeight - r.bottom - GAP - MARGIN;
    const above = r.top - GAP - MARGIN;
    const wanted = Math.min(menu.scrollHeight, MAX_HEIGHT);
    const up = below < wanted && above > below;

    // As wide as the longest option, never narrower than the field: a ghost field is sized to its
    // value and would squeeze its options. Kept inside the window, so one at the right edge opens
    // leftward rather than past it.
    menu.style.position = 'fixed';
    menu.style.right = 'auto';
    menu.style.width = 'max-content';
    menu.style.minWidth = `${r.width}px`;
    const w = menu.offsetWidth;
    menu.style.left = `${Math.max(MARGIN, Math.min(r.left, window.innerWidth - MARGIN - w))}px`;
    menu.style.maxHeight = `${Math.max(0, Math.min(MAX_HEIGHT, up ? above : below))}px`;
    if (up) {
        menu.style.top = 'auto';
        menu.style.bottom = `${window.innerHeight - r.top + GAP}px`;
    } else {
        menu.style.top = `${r.bottom + GAP}px`;
        menu.style.bottom = 'auto';
    }
}

export function init(trigger) {
    if (!trigger || trigger._indxSelect) return;
    const state = { menu: null };

    // While open, follow the field: a scroll anywhere (the modal's own, the page's) or a resize
    // moves it, and a fixed menu would otherwise stay behind.
    const follow = () => { if (state.menu) place(trigger, state.menu); };

    const observer = new MutationObserver(() => {
        const menu = trigger.querySelector(':scope > .indx-select-menu');
        if (menu === state.menu) return;
        state.menu = menu;
        if (menu) {
            place(trigger, menu);
            window.addEventListener('scroll', follow, true);
            window.addEventListener('resize', follow);
        } else {
            window.removeEventListener('scroll', follow, true);
            window.removeEventListener('resize', follow);
        }
    });
    observer.observe(trigger, { childList: true });

    // Arrow keys inside the open menu, here rather than in .NET: moving focus needs no state the
    // server holds, and a round-trip per key would lag. Escape and opening stay in .NET.
    const keydown = event => {
        const menu = state.menu;
        if (!menu || !['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
        const options = [...menu.querySelectorAll('[role="option"]')];
        if (options.length === 0) return;
        event.preventDefault();
        const at = options.indexOf(document.activeElement);
        let next;
        if (event.key === 'Home') next = 0;
        else if (event.key === 'End') next = options.length - 1;
        else if (at < 0) next = Math.max(0, options.findIndex(o => o.getAttribute('aria-selected') === 'true'));
        else next = Math.min(options.length - 1, Math.max(0, at + (event.key === 'ArrowDown' ? 1 : -1)));
        options[next].focus();
    };
    trigger.addEventListener('keydown', keydown);
    trigger._indxSelect = true;

    // Disposed through this handle rather than through the element: on an enhanced navigation
    // the element is already gone when .NET disposes, and the window listeners of an open menu
    // would outlive the page.
    return {
        dispose() {
            observer.disconnect();
            window.removeEventListener('scroll', follow, true);
            window.removeEventListener('resize', follow);
            trigger.removeEventListener('keydown', keydown);
            delete trigger._indxSelect;
        }
    };
}

// Focus the chosen option of an open menu, or the first: the field was opened from the keyboard.
export function focusOption(trigger) {
    const menu = trigger?.querySelector(':scope > .indx-select-menu');
    if (!menu) return;
    const options = [...menu.querySelectorAll('[role="option"]')];
    (options.find(o => o.getAttribute('aria-selected') === 'true') ?? options[0])?.focus({ preventScroll: true });
}

