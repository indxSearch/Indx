// Browser-side helpers behind IndxCloudApi.Services.BrowserFiles.
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
