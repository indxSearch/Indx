// Browser-side helpers behind IndxServer.Services.BrowserFiles.
window.indxTriggerFileInput = function (id) {
    document.getElementById(id)?.click();
};

window.indxDownloadFile = function (filename, content, contentType) {
    const blob = new Blob([content], { type: contentType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};

window.copyToClipboard = function (text) {
    if (navigator.clipboard && window.isSecureContext) {
        return navigator.clipboard.writeText(text);
    }
    // Fallback for non-secure contexts / older browsers
    const textArea = document.createElement("textarea");
    textArea.value = text;
    textArea.style.position = "fixed";
    textArea.style.left = "-999999px";
    document.body.appendChild(textArea);
    textArea.focus();
    textArea.select();
    try {
        document.execCommand('copy');
        textArea.remove();
        return Promise.resolve();
    } catch (error) {
        textArea.remove();
        return Promise.reject(error);
    }
};

// The browser's own "Leave site?" prompt, armed only while a file is being sent from this page.
// Those bytes travel browser → circuit → server, so leaving loses them. Load and index are
// server-owned and survive a reopened page, which is why this is not armed for them.
let indxLeaveHandler = null;
window.indxSetLeaveWarning = function (on) {
    if (on && !indxLeaveHandler) {
        // Browsers ignore custom text here and show their own wording; preventDefault plus a
        // returnValue is what still arms it across all of them.
        indxLeaveHandler = function (e) { e.preventDefault(); e.returnValue = ''; };
        window.addEventListener('beforeunload', indxLeaveHandler);
    } else if (!on && indxLeaveHandler) {
        window.removeEventListener('beforeunload', indxLeaveHandler);
        indxLeaveHandler = null;
    }
};
