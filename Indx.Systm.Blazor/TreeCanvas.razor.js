// TreeCanvas's browser half. The layout is computed in C#; this module measures the pills for it,
// and owns the view: the tree drawn on a board that pans and zooms by transform, as a design canvas
// does, with the grid following. The React TreeCanvas has the same logic in viewport.ts; keep the
// two alike.
//
// The view lives on elements Blazor renders without a style (the view and the grid layers), so a
// re-render (a node opened, a count changed) never resets it, and panning costs no round trip.
//
//   drag the board (mouse or pen)   pan; a press that moves under four pixels is a click
//   two-finger scroll, wheel        pan; Shift with the wheel pans sideways
//   pinch, Ctrl or Cmd with wheel   zoom at the pointer
//   arrows on the board             pan
//   + and -                         zoom at the middle; 0 fits the tree in view
//   the controls                    zoom in, zoom out, fit, and the zoom shown (click for 100%)

const MIN_ZOOM = 0.2;
const MAX_ZOOM = 2;
const FIT_PAD = 24;     // room kept around the tree when it is fitted, and around a node brought into view
const STEP = 1.2;       // one press of zoom in or out
const KEY_PAN = 48;     // one arrow press, in screen pixels
const GRID_MIN = 8;     // the grid's tile on screen is kept at least this: zoomed out, its spacing doubles
const states = new WeakMap();

// Reports each pill's laid-out width (unaffected by the zoom) to .NET, so the columns are placed by
// what the text really measures rather than a guess.
export function measure(sheet) {
    if (!sheet) return {};
    const widths = {};
    sheet.querySelectorAll('[data-node]').forEach((el) => { widths[el.dataset.node] = el.offsetWidth; });
    return widths;
}

// Wires a canvas once; later calls only update whether it pans. A canvas drawn anew (back from the
// list view) is a new element and is wired again.
export function attach(canvas, view, grid, pan) {
    if (!canvas || !view) return;
    const known = states.get(canvas);
    if (known && known.view === view) { known.pan = pan; return; }
    const s = { canvas, view, grid, x: 0, y: 0, z: 1, pan };
    states.set(canvas, s);
    wire(s);
    apply(s);
}

// The first view of a tree: fitted when that leaves it readable; else at the given zoom (1 when
// none), from the left, with the first root a third of the way down.
export function initial(canvas, zoom) {
    const s = states.get(canvas);
    if (!s) return;
    const { cw, ch, sw, sh, cl } = sizes(s);
    if (zoom == null) {
        const fit = fitZoom(cw, ch, sw, sh);
        if (fit >= 0.6) { fitInto(s, fit); return; }
    }
    s.z = clamp(zoom ?? 1);
    s.x = cl;
    const root = s.view.querySelector('[aria-level="1"]');
    const rootY = root ? (root.parentElement.offsetTop + root.offsetHeight / 2) * s.z : 0;
    s.y = Math.min(0, Math.max(ch - sh * s.z, ch / 3 - rootY));
    if (sh * s.z <= ch) s.y = (ch - sh * s.z) / 2;
    apply(s);
}

export function command(canvas, action) {
    const s = states.get(canvas);
    if (!s) return;
    const r = canvas.getBoundingClientRect();
    const cx = r.width / 2, cy = r.height / 2;
    if (action === 'in') zoomAt(s, s.z * STEP, cx, cy);
    else if (action === 'out') zoomAt(s, s.z / STEP, cx, cy);
    else if (action === 'reset') zoomAt(s, 1, cx, cy);
    else if (action === 'fit') { const { cw, ch, sw, sh } = sizes(s); fitInto(s, fitZoom(cw, ch, sw, sh)); }
    else if (action === 'fullscreen') toggleFullscreen(canvas);
}

// The board fills the screen: the element the page marks data-tree-fullscreen (its panel goes
// along), else the board. The view is fitted again once the size has changed, in and out.
function toggleFullscreen(canvas) {
    if (document.fullscreenElement) { document.exitFullscreen?.(); return; }
    const target = canvas.closest('[data-tree-fullscreen]') ?? canvas;
    const refit = () => {
        document.removeEventListener('fullscreenchange', refit);
        requestAnimationFrame(() => command(canvas, 'fit'));
        if (document.fullscreenElement) document.addEventListener('fullscreenchange', refit);
    };
    document.addEventListener('fullscreenchange', refit);
    target.requestFullscreen?.().catch(() => document.removeEventListener('fullscreenchange', refit));
}

// Focus a node without the browser's own scroll, and pan just enough to bring it into view.
export function focusNode(sheet, id) {
    if (!sheet) return;
    const el = sheet.querySelector(`[data-node="${CSS.escape(id)}"]`);
    if (!el) return;
    el.focus({ preventScroll: true });
    const canvas = sheet.closest('.indx-tree-canvas');
    const s = canvas && states.get(canvas);
    if (s) bringIntoView(s, el);
}

function wire(s) {
    const { canvas } = s;
    let start = null;
    let moved = false;

    canvas.addEventListener('pointerdown', (e) => {
        moved = false;
        if (!s.pan || e.button !== 0 || e.pointerType === 'touch') return;
        if (e.target.closest('button, a, input, select, textarea, .indx-tree-controls')) return;
        start = { px: e.clientX, py: e.clientY, x: s.x, y: s.y, id: e.pointerId };
    });
    canvas.addEventListener('pointermove', (e) => {
        if (!start || e.pointerId !== start.id) return;
        const dx = e.clientX - start.px, dy = e.clientY - start.py;
        if (!moved) {
            if (Math.hypot(dx, dy) < 4) return;
            moved = true;
            canvas.setPointerCapture(e.pointerId);
            canvas.dataset.panning = '';
        }
        s.x = start.x + dx;
        s.y = start.y + dy;
        apply(s);
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
        if (moved) { moved = false; e.stopPropagation(); e.preventDefault(); return; }
        const action = e.target.closest('[data-tree-action]');
        if (action && canvas.contains(action)) command(canvas, action.dataset.treeAction);
    }, true);

    canvas.addEventListener('wheel', (e) => {
        if (!s.pan) return;
        e.preventDefault();
        const unit = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? canvas.clientHeight : 1;
        if (e.ctrlKey || e.metaKey) {
            // A pinch arrives as a wheel with Ctrl and small deltas; a mouse notch as about 100, held
            // to a quarter of that so one notch zooms by about 28%.
            const d = Math.max(-25, Math.min(25, e.deltaY * unit));
            const r = canvas.getBoundingClientRect();
            zoomAt(s, s.z * Math.exp(-d * 0.01), e.clientX - r.left, e.clientY - r.top);
        } else {
            s.x -= (e.shiftKey && !e.deltaX ? e.deltaY : e.deltaX) * unit;
            s.y -= (e.shiftKey && !e.deltaX ? 0 : e.deltaY) * unit;
            apply(s);
        }
    }, { passive: false });

    canvas.addEventListener('keydown', (e) => {
        if (!s.pan || e.altKey || e.ctrlKey || e.metaKey) return;
        if (e.target.closest('input, select, textarea')) return;
        const r = canvas.getBoundingClientRect();
        if (e.key === '+' || e.key === '=') zoomAt(s, s.z * STEP, r.width / 2, r.height / 2);
        else if (e.key === '-' || e.key === '_') zoomAt(s, s.z / STEP, r.width / 2, r.height / 2);
        else if (e.key === '0') command(canvas, 'fit');
        else if (e.target === canvas && e.key.startsWith('Arrow')) {
            // On the board itself; on a node the arrows move between nodes.
            if (e.key === 'ArrowLeft') s.x += KEY_PAN;
            if (e.key === 'ArrowRight') s.x -= KEY_PAN;
            if (e.key === 'ArrowUp') s.y += KEY_PAN;
            if (e.key === 'ArrowDown') s.y -= KEY_PAN;
            apply(s);
        }
        else return;
        e.preventDefault();
    });

    // Tab moves focus to a node out of view, and the browser scrolls the canvas to it even though
    // it does not scroll: take the scroll as a pan, so the view and the focus agree.
    canvas.addEventListener('scroll', () => {
        if (!canvas.scrollLeft && !canvas.scrollTop) return;
        s.x -= canvas.scrollLeft;
        s.y -= canvas.scrollTop;
        canvas.scrollLeft = 0;
        canvas.scrollTop = 0;
        apply(s);
    });
}

// The controls sit on the board's left edge; a fitted tree keeps clear of them (on a phone the
// tree came under them): cw is the width right of them.
function sizes(s) {
    const sheet = s.view.firstElementChild;
    const controls = s.canvas.querySelector('.indx-tree-controls');
    const cl = controls ? controls.offsetLeft + controls.offsetWidth : 0;
    return { cw: s.canvas.clientWidth - cl, ch: s.canvas.clientHeight, sw: sheet?.offsetWidth ?? 0, sh: sheet?.offsetHeight ?? 0, cl };
}

function fitZoom(cw, ch, sw, sh) {
    if (!sw || !sh) return 1;
    return clamp(Math.min((cw - FIT_PAD * 2) / sw, (ch - FIT_PAD * 2) / sh, 1));
}

function fitInto(s, z) {
    const { cw, ch, sw, sh, cl } = sizes(s);
    s.z = z;
    s.x = cl + (cw - sw * z) / 2;
    s.y = (ch - sh * z) / 2;
    apply(s);
}

function zoomAt(s, z, px, py) {
    z = clamp(z);
    s.x = px - (px - s.x) * (z / s.z);
    s.y = py - (py - s.y) * (z / s.z);
    s.z = z;
    apply(s);
}

function bringIntoView(s, el) {
    const c = s.canvas.getBoundingClientRect();
    const r = el.getBoundingClientRect();
    if (r.left < c.left + FIT_PAD) s.x += c.left + FIT_PAD - r.left;
    else if (r.right > c.right - FIT_PAD) s.x -= r.right - (c.right - FIT_PAD);
    if (r.top < c.top + FIT_PAD) s.y += c.top + FIT_PAD - r.top;
    else if (r.bottom > c.bottom - FIT_PAD) s.y -= r.bottom - (c.bottom - FIT_PAD);
    apply(s);
}

function apply(s) {
    s.view.style.transform = `translate(${s.x}px, ${s.y}px) scale(${s.z})`;
    if (s.grid) {
        s.grid.style.setProperty('--tree-x', `${s.x}px`);
        s.grid.style.setProperty('--tree-y', `${s.y}px`);
        s.grid.style.setProperty('--tree-zoom', `${gridScale(s)}`);
    }
    const label = s.canvas.querySelector('[data-tree-zoom-label]');
    if (label) label.textContent = `${Math.round(s.z * 100)}%`;
}

// The grid's scale: the zoom, doubled until a tile is at least GRID_MIN on screen, so zoomed out it
// thins as a design canvas's does rather than packing into a grey.
function gridScale(s) {
    s.tile ??= parseFloat(getComputedStyle(s.grid).getPropertyValue('--indx-tree-tile')) || 12;
    let g = s.z;
    while (s.tile * g < GRID_MIN) g *= 2;
    return g;
}

function clamp(z) { return Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, z)); }
