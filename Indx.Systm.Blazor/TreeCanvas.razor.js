// Reports each pill's rendered width to .NET, so the layout (computed in C#) can place the
// columns by what the text really measures rather than a guess.

export function measure(sheet) {
    if (!sheet) return {};
    const widths = {};
    sheet.querySelectorAll('[data-node]').forEach((el) => {
        widths[el.dataset.node] = Math.ceil(el.getBoundingClientRect().width);
    });
    return widths;
}

export function focusNode(sheet, id) {
    if (!sheet) return;
    const el = sheet.querySelector(`[data-node="${CSS.escape(id)}"]`);
    if (el) el.focus();
}
