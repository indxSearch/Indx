// TagsInput's browser half: puts new text in the box (empty after a value is made, the last value
// when Backspace takes it back) without replacing the element, so the focus and the caret stay.
export function setText(input, text) {
    if (!input) return;
    input.value = text;
    input.setSelectionRange(text.length, text.length);
}
