export function focusById(id) {
    document.getElementById(id)?.focus({ preventScroll: true });
}

export function init(el) {
    const handler = event => {
        if (event.target.matches('.indx-datepicker-day') && ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown'].includes(event.key)) event.preventDefault();
    };
    el._indxDateKeydown = handler;
    el.addEventListener('keydown', handler);
}

export function dispose(el) {
    el.removeEventListener('keydown', el._indxDateKeydown);
    delete el._indxDateKeydown;
}
