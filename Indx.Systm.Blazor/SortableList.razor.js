// SortableList's browser half: the drag and the keys. Pointer events on the list, read by the
// grip that was pressed (data-sortable-grip). While an item is held the whole page has the move
// cursor, the item fades and a line shows where it lands, all set here with no round trip; .NET
// hears the move once, when the item is dropped (Moved). The same rules as the React SortableList.

const lists = new Map();   // ul -> teardown

const rows = (ul) => [...ul.children].filter((li) => li.classList.contains('indx-sortable-item'));

export function attach(ul, dotnet) {
    if (lists.has(ul)) return;
    let held = null;   // { from, over: { i, after }, pointerId, grip }

    const clear = () => {
        rows(ul).forEach((li) => { li.removeAttribute('data-drop'); li.removeAttribute('data-dragging'); });
        document.documentElement.classList.remove('cursor-move', 'indx-sortable-dragging');
    };
    const at = (y) => {
        const all = rows(ul);
        for (let i = 0; i < all.length; i++) {
            const r = all[i].getBoundingClientRect();
            if (y >= r.top && y < r.bottom) return { i, after: y > r.top + r.height / 2 };
        }
        const first = all[0]?.getBoundingClientRect();
        if (first && y < first.top) return { i: 0, after: false };
        return { i: all.length - 1, after: true };
    };
    const show = () => {
        rows(ul).forEach((li, i) => {
            const { from, over } = held;
            // No line where the drop would not move the item.
            const still = i === from || (over.after ? from === i + 1 : from === i - 1);
            if (over.i === i && !still) li.setAttribute('data-drop', over.after ? 'after' : 'before');
            else li.removeAttribute('data-drop');
        });
    };
    const target = (from, over) => {
        let to = over.i + (over.after ? 1 : 0);
        if (to > from) to--;
        return to;
    };
    const tell = (from, to) => { if (from !== to) dotnet.invokeMethodAsync('Moved', from, to).catch(() => { }); };

    const onDown = (e) => {
        const grip = e.target.closest('[data-sortable-grip]');
        if (!grip || !ul.contains(grip) || e.button !== 0) return;
        e.preventDefault();
        const from = Number(grip.dataset.sortableGrip);
        try { grip.setPointerCapture(e.pointerId); } catch { }
        held = { from, over: { i: from, after: false }, pointerId: e.pointerId, grip };
        rows(ul)[from]?.setAttribute('data-dragging', '');
        document.documentElement.classList.add('cursor-move', 'indx-sortable-dragging');
    };
    const onMove = (e) => {
        if (!held || e.pointerId !== held.pointerId) return;
        held.over = at(e.clientY);
        show();
    };
    const onUp = (e) => {
        if (!held || e.pointerId !== held.pointerId) return;
        const { from, over, grip } = held;
        held = null;
        try { grip.releasePointerCapture(e.pointerId); } catch { }
        clear();
        if (e.type === 'pointerup') tell(from, target(from, over));
    };
    const onKey = (e) => {
        const grip = e.target.closest('[data-sortable-grip]');
        if (!grip || !ul.contains(grip)) return;
        if (e.key === 'Escape' && held) { held = null; clear(); return; }
        const i = Number(grip.dataset.sortableGrip);
        const count = rows(ul).length;
        if (e.key === 'ArrowUp' && i > 0) { e.preventDefault(); tell(i, i - 1); }
        if (e.key === 'ArrowDown' && i < count - 1) { e.preventDefault(); tell(i, i + 1); }
    };

    ul.addEventListener('pointerdown', onDown);
    ul.addEventListener('pointermove', onMove);
    ul.addEventListener('pointerup', onUp);
    ul.addEventListener('pointercancel', onUp);
    ul.addEventListener('keydown', onKey);
    // Attached: a press from here on is heard (the test waits for it; the import is asynchronous).
    ul.setAttribute('data-sortable-ready', '');
    lists.set(ul, () => {
        ul.removeEventListener('pointerdown', onDown);
        ul.removeEventListener('pointermove', onMove);
        ul.removeEventListener('pointerup', onUp);
        ul.removeEventListener('pointercancel', onUp);
        ul.removeEventListener('keydown', onKey);
        clear();
    });
}

export function dispose(ul) {
    lists.get(ul)?.();
    lists.delete(ul);
}
