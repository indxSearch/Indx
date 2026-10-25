// Takes an element to the browser's full screen and back (Components/Datasets/BrowseTab.razor):
// the Browse frame is already as tall as the window leaves, and full screen is the rest. The
// component hears each change, since Escape leaves full screen without a click.
window.indxFullscreen = {
    toggle: function (el) {
        if (!el) return;
        if (document.fullscreenElement) { document.exitFullscreen(); return; }
        if (el.requestFullscreen) el.requestFullscreen().catch(function () { });
    },
    watch: function (el, dotNetRef) {
        if (!el) return;
        var handler = function () {
            dotNetRef.invokeMethodAsync('OnFullscreenChanged', document.fullscreenElement === el).catch(function () { });
        };
        document.addEventListener('fullscreenchange', handler);
        el._indxFullscreenHandler = handler;
    },
    unwatch: function (el) {
        if (!el || !el._indxFullscreenHandler) return;
        document.removeEventListener('fullscreenchange', el._indxFullscreenHandler);
        delete el._indxFullscreenHandler;
    }
};
