// Reports each pill's rendered width to .NET, so the layout (computed in C#) can place the
// columns by what the text really measures rather than a guess.
// And pans the canvas by dragging (pan, below).

export function measure(sheet) {
    if (!sheet) return {};
    const widths = {};
    sheet.querySelectorAll('[data-node]').forEach((el) => {
        widths[el.dataset.node] = Math.ceil(el.getBoundingClientRect().width);
    });
    return widths;
}

export function focusNode(sheet, id) {
    if (!sheet) return;
    const el = sheet.querySelector(`[data-node="${CSS.escape(id)}"]`);
    if (el) el.focus();
}

// Drag to pan: a mouse or pen held down on the canvas moves the tree under it, as a design canvas
// does; touch keeps the browser's own scrolling, and the wheel still scrolls. A press that moves
// less than four pixels is a click, so a pill is still chosen by clicking it. The buttons in the
// tree (open, close, "and N more") never start a drag. Called after each render; the element
// carries its listeners, so a canvas that is redrawn gets them again and a removed one drops them.
export function pan(canvas) {
    if (!canvas || canvas.__indxPan) return;
    canvas.__indxPan = true;
    let start = null;
    let moved = false;
    canvas.addEventListener('pointerdown', (e) => {
        moved = false;
        if (e.button !== 0 || e.pointerType === 'touch') return;
        if (e.target.closest('button, a, input, select, textarea')) return;
        start = { x: e.clientX, y: e.clientY, left: canvas.scrollLeft, top: canvas.scrollTop, id: e.pointerId };
    });
    canvas.addEventListener('pointermove', (e) => {
        if (!start || e.pointerId !== start.id) return;
        const dx = e.clientX - start.x, dy = e.clientY - start.y;
        if (!moved) {
            if (Math.hypot(dx, dy) < 4) return;
            moved = true;
            canvas.setPointerCapture(e.pointerId);
            canvas.dataset.panning = '';
        }
        canvas.scrollLeft = start.left - dx;
        canvas.scrollTop = start.top - dy;
    });
    const end = (e) => {
        if (!start || e.pointerId !== start.id) return;
        start = null;
        delete canvas.dataset.panning;
    };
    canvas.addEventListener('pointerup', end);
    canvas.addEventListener('pointercancel', end);
    // The click that ends a drag is not a choice: stopped before it bubbles to Blazor's handler.
    canvas.addEventListener('click', (e) => {
        if (!moved) return;
        moved = false;
        e.stopPropagation();
        e.preventDefault();
    }, true);
}
