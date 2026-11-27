// Tells an element how far down the page it starts, so CSS can give it "the rest of the window".
//
// Mark the element with data-fill-viewport. It gets a --fill-top custom property (its distance
// from the top of the document, in px), kept current as the page changes. The stylesheet does
// the rest, for example: height: calc(100dvh - var(--fill-top) - <what comes below>).
//
// Why measure: what sits above such an element belongs to other components (header, breadcrumb
// row, tab bar, an alert now and then) and changes height with the screen. A breadcrumb that
// wraps onto a second line on a phone pushed the Search preview down by one row, and a number
// written into the stylesheet then left the page scrolling by exactly that row, over nothing.
//
// No interop: Blazor adds and removes these elements as tabs change, so the script watches the
// document itself. Measuring is cheap and runs at most once per frame.
(function () {
    function measure() {
        var marked = document.querySelectorAll('[data-fill-viewport]');
        for (var i = 0; i < marked.length; i++) {
            var top = marked[i].getBoundingClientRect().top + window.scrollY;
            var value = (Math.round(top * 10) / 10) + 'px';
            if (marked[i].style.getPropertyValue('--fill-top') !== value)
                marked[i].style.setProperty('--fill-top', value);
        }
    }

    // Once per frame, with a timer beside it: a tab in the background gets no animation frames,
    // and the measurement should still be right when it comes to the front.
    var scheduled = false;
    function run() {
        if (!scheduled) return;
        scheduled = false;
        measure();
    }
    function schedule() {
        if (scheduled) return;
        scheduled = true;
        requestAnimationFrame(run);
        setTimeout(run, 120);
    }

    new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
    if (window.ResizeObserver) new ResizeObserver(schedule).observe(document.documentElement);
    window.addEventListener('resize', schedule);
    window.addEventListener('orientationchange', schedule);
    window.addEventListener('load', schedule);
    schedule();
})();
