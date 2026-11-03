// Resizable's browser half: the drag, the keys and the kept size. The first pane's share lives in
// the root's --indx-resizable-size, set here while dragging so it follows the pointer with no round
// trip; once it settles (pointer up, a key) .NET is told, and the size kept under the storage key.

const roots = new Map();   // root -> its teardown
const STEP = 2, BIG = 10;   // percent per arrow, per Page key

export function init(root, dotnet, direction, min, max, storageKey) {
    dispose(root);
    const handle = root.querySelector(':scope > .indx-resizable-handle');
    if (!handle) return null;
    const horizontal = direction !== 'vertical';
    const clamp = (v) => Math.min(max, Math.max(min, v));
    let size = parseFloat(getComputedStyle(root).getPropertyValue('--indx-resizable-size')) || 50;
    const set = (v) => {
        size = clamp(v);
        root.style.setProperty('--indx-resizable-size', `${size}%`);
        handle.setAttribute('aria-valuenow', String(Math.round(size)));
    };
    const settle = () => {
        if (storageKey) { try { localStorage.setItem(storageKey, String(size)); } catch { } }
        dotnet.invokeMethodAsync('Settled', size).catch(() => { });
    };

    const onDown = (e) => {
        if (e.button !== 0) return;
        e.preventDefault();
        handle.setPointerCapture(e.pointerId);
        root.setAttribute('data-dragging', '');
        const box = root.getBoundingClientRect();
        const onMove = (m) => set(horizontal ? ((m.clientX - box.left) / box.width) * 100 : ((m.clientY - box.top) / box.height) * 100);
        const onUp = () => {
            handle.removeEventListener('pointermove', onMove);
            handle.removeEventListener('pointerup', onUp);
            handle.removeEventListener('pointercancel', onUp);
            root.removeAttribute('data-dragging');
            settle();
        };
        handle.addEventListener('pointermove', onMove);
        handle.addEventListener('pointerup', onUp);
        handle.addEventListener('pointercancel', onUp);
    };
    const onKey = (e) => {
        const less = horizontal ? 'ArrowLeft' : 'ArrowUp', more = horizontal ? 'ArrowRight' : 'ArrowDown';
        let to = null;
        if (e.key === less) to = size - STEP;
        else if (e.key === more) to = size + STEP;
        else if (e.key === 'PageUp') to = size - BIG;
        else if (e.key === 'PageDown') to = size + BIG;
        else if (e.key === 'Home') to = min;
        else if (e.key === 'End') to = max;
        if (to === null) return;
        e.preventDefault();
        set(to);
        settle();
    };
    handle.addEventListener('pointerdown', onDown);
    handle.addEventListener('keydown', onKey);
    roots.set(root, () => {
        handle.removeEventListener('pointerdown', onDown);
        handle.removeEventListener('keydown', onKey);
    });

    if (storageKey) {
        try {
            const v = parseFloat(localStorage.getItem(storageKey) ?? '');
            if (!Number.isNaN(v)) { set(v); return size; }
        } catch { }
    }
    return null;
}

export function dispose(root) {
    roots.get(root)?.();
    roots.delete(root);
}
