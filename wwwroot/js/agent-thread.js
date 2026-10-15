// Keeps the Agent panel's thread at its newest turn (Components/Systm/AgentPanel.razor): the
// panel asks for it after a render that grew the thread or opened the panel. A scrollIntoView
// asked for through interop in that same instant was lost whenever the thread was long (15 Oct
// 2026), so this scrolls the thread itself, on the next frames and once more after the panel's
// 160ms slide (app.css .shell-agent), and sets scrollTop rather than scrolling an element into
// view, which no ancestor's state can swallow.
window.indxAgent = {
    scrollToEnd: function (marker) {
        var thread = marker && marker.closest ? marker.closest('.indx-chat-thread') : null;
        if (!thread) return;
        function end() { thread.scrollTop = thread.scrollHeight; }
        requestAnimationFrame(function () { end(); requestAnimationFrame(end); });
        setTimeout(end, 200);
    }
};
