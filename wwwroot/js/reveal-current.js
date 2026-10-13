// Keeps the open section in sight in a menu that scrolls sideways: on a phone the side column
// folds into one row (Shared/SideMenu.razor.css), and a section far along it, Options or Search
// preview, opened out of sight, so the page did not show where you were.
//
// No interop, like fill-viewport.js: the menu is redrawn by an island as sections change, so the
// script watches the document. It moves the row only when the current item changes (or the window
// is resized), never while someone is scrolling it, and only sideways: the page itself stays put.
(function () {
    var last = null;
    function reveal() {
        var current = document.querySelector('.side-menu [aria-current="page"]');
        if (!current || current === last) return;
        last = current;
        var row = current.closest('.side-menu');
        if (!row || row.scrollWidth <= row.clientWidth) return;
        var item = current.getBoundingClientRect(), box = row.getBoundingClientRect(), pad = 16;
        if (item.left < box.left + pad) row.scrollLeft += item.left - box.left - pad;
        else if (item.right > box.right - pad) row.scrollLeft += item.right - box.right + pad;
    }
    var queued = false;
    function schedule() {
        if (queued) return;
        queued = true;
        requestAnimationFrame(function () { queued = false; reveal(); });
    }
    new MutationObserver(schedule).observe(document.documentElement, {
        subtree: true, childList: true, attributes: true, attributeFilter: ['aria-current'],
    });
    window.addEventListener('resize', function () { last = null; schedule(); });
    schedule();
})();
