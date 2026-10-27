// Combobox's browser half: the open list is at least as wide as the field, and its keys are the
// command palette's own (arrows, Home, End, Enter, Escape, the pointer), from CommandPalette.razor.js.
import { opened as paletteOpened, reset } from './CommandPalette.razor.js';

export function opened(panel, dotnet, field) {
    const popover = panel?.closest('.indx-popover');
    if (popover && field) popover.style.minWidth = `${Math.max(240, field.offsetWidth)}px`;
    paletteOpened(panel, dotnet);
}

export { reset };
