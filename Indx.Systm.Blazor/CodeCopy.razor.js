// Clipboard for CodeCopy. The Clipboard API needs a secure context, and a self-hosted server is
// often reached over plain http, so a hidden textarea and execCommand cover that case.
export async function copy(text) {
    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // fall through to the textarea
        }
    }
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.left = '-9999px';
    document.body.appendChild(area);
    area.select();
    try {
        return document.execCommand('copy');
    } catch {
        return false;
    } finally {
        area.remove();
    }
}
