// ScrubSlider's browser half: the drag and the keys. The ruler moves with the hand, as a tape does:
// a drag to the right brings lower values in under the handle. While dragging, the ruler's variables
// and the marks are set here, so they follow the pointer with no round trip; .NET hears every new
// value (Moved) and the settled one when the drag ends (Settled).

const sliders = new Map();   // ruler -> state

const fmt = (v) => String(Number(v.toFixed(8)));

function draw(s, value) {
    const { ruler, opts } = s;
    const toPx = (at) => ((at - value) / opts.step) * opts.tick;
    const period = opts.tick * opts.major;
    ruler.style.setProperty('--scrub-shift', `${-(((value / opts.step) * opts.tick) % period)}px`);
    const left = typeof opts.min === 'number' ? `max(0px, calc(50% + ${toPx(opts.min)}px))` : '0px';
    const right = typeof opts.max === 'number' ? `max(0px, calc(50% - ${toPx(opts.max)}px))` : '0px';
    ruler.style.setProperty('--scrub-clip', `inset(0 ${right} 0 ${left})`);
    ruler.querySelectorAll('.indx-scrub-mark').forEach((m) => { m.style.left = `calc(50% + ${toPx(Number(m.dataset.value))}px)`; });
    ruler.setAttribute('aria-valuenow', fmt(value));
    const field = ruler.parentElement?.querySelector('.indx-scrub-field input');
    if (field && document.activeElement !== field) field.value = fmt(value);
}

export function sync(ruler, dotnet, opts) {
    let s = sliders.get(ruler);
    if (!s) {
        s = { ruler, dotnet, opts, value: opts.value, dragging: false };
        const clamp = (v) => {
            let r = v;
            if (typeof s.opts.min === 'number') r = Math.max(s.opts.min, r);
            if (typeof s.opts.max === 'number') r = Math.min(s.opts.max, r);
            return Number(r.toFixed(s.opts.decimals));
        };
        const move = (next, settle) => {
            if (next !== s.value) {
                s.value = next;
                draw(s, next);
                s.dotnet.invokeMethodAsync('Moved', next).catch(() => { });
            }
            if (settle) s.dotnet.invokeMethodAsync('Settled', s.value).catch(() => { });
        };
        const onDown = (e) => {
            if (s.opts.disabled || e.button !== 0) return;
            e.preventDefault();
            ruler.focus();
            try { ruler.setPointerCapture(e.pointerId); } catch { }
            const x = e.clientX, from = s.value;
            let moved = false;
            s.dragging = true;
            ruler.classList.add('indx-scrub-ruler--dragging');
            const onMove = (m) => {
                const ticks = Math.round((m.clientX - x) / s.opts.tick);
                if (ticks !== 0) moved = true;
                move(clamp(from - ticks * s.opts.step), false);
            };
            const onUp = () => {
                window.removeEventListener('pointermove', onMove);
                window.removeEventListener('pointerup', onUp);
                window.removeEventListener('pointercancel', onUp);
                try { ruler.releasePointerCapture(e.pointerId); } catch { }
                s.dragging = false;
                ruler.classList.remove('indx-scrub-ruler--dragging');
                if (moved) s.dotnet.invokeMethodAsync('Settled', s.value).catch(() => { });
            };
            window.addEventListener('pointermove', onMove);
            window.addEventListener('pointerup', onUp);
            window.addEventListener('pointercancel', onUp);
        };
        const onKey = (e) => {
            if (s.opts.disabled) return;
            ruler.classList.add('indx-scrub-ruler--keyboard');
            const big = e.shiftKey ? 10 : 1;
            let next;
            if (e.key === 'ArrowRight' || e.key === 'ArrowUp') next = clamp(s.value + s.opts.step * big);
            else if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') next = clamp(s.value - s.opts.step * big);
            else if (e.key === 'Home' && typeof s.opts.min === 'number') next = s.opts.min;
            else if (e.key === 'End' && typeof s.opts.max === 'number') next = s.opts.max;
            if (next === undefined) return;
            e.preventDefault();
            move(next, true);
        };
        const onBlur = () => ruler.classList.remove('indx-scrub-ruler--keyboard');
        ruler.addEventListener('pointerdown', onDown);
        ruler.addEventListener('keydown', onKey);
        ruler.addEventListener('blur', onBlur);
        s.teardown = () => {
            ruler.removeEventListener('pointerdown', onDown);
            ruler.removeEventListener('keydown', onKey);
            ruler.removeEventListener('blur', onBlur);
        };
        sliders.set(ruler, s);
    }
    s.dotnet = dotnet;
    s.opts = opts;
    // A value from .NET is taken unless a drag is running, which owns the value until it ends.
    if (!s.dragging) { s.value = opts.value; draw(s, s.value); }
}

export function dispose(ruler) {
    sliders.get(ruler)?.teardown?.();
    sliders.delete(ruler);
}
