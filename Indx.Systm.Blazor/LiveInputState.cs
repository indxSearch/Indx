namespace Indx.Systm.Blazor
{
    /// <summary>
    /// Keeps a search-as-you-type input from eating keystrokes on a slow connection.
    ///
    /// The obvious markup, <c>value="@text" @oninput="..."</c>, makes the server the owner of the
    /// box. Each keystroke is sent up, the handler runs (a search, say), and the re-render carries
    /// <c>value</c> back down. With any latency that answer describes the past: the user has typed
    /// on, Blazor sees the attribute change from "ham" to "hamb" and writes "hamb" into a box that
    /// already says "hambu". The next letter lands on the stale text, and "hamburger" comes out
    /// "hambuger". The slower the handler, the more often it happens, which is why a search box
    /// shows it and a plain form field rarely does.
    ///
    /// The cure is to not send the box what the user just typed. Blazor only touches a DOM value
    /// when the rendered attribute differs from the previous render, so <see cref="Rendered"/> is
    /// held still while text arrives by typing, and the browser remains the owner of the box.
    /// Only a change from outside (Clear, a reset by the parent) is rendered, and it bumps
    /// <see cref="Key"/> as well: the rendered string may be the same as last time ("" before
    /// typing, "" after Clear), in which case the diff would send nothing and the typed text
    /// would stay. A new key replaces the element, which always shows the new value.
    ///
    /// Use: <c>&lt;input @key="state.Key" value="@state.Rendered" @oninput="e => state.Typed(...)" /&gt;</c>.
    /// </summary>
    public sealed class LiveInputState
    {
        /// <summary>What to put in the <c>value</c> attribute. Does not follow typing.</summary>
        public string? Rendered { get; private set; }
        /// <summary>The text as far as the server knows: the last typed or externally set value.</summary>
        public string? Text { get; private set; }
        /// <summary>Put on the input as <c>@key</c>; changes when the element must be replaced.</summary>
        public int Key { get; private set; }

        public LiveInputState(string? initial = null) { Rendered = initial; Text = initial; }

        /// <summary>The user typed. The box already shows it, so nothing about the rendering changes.</summary>
        public void Typed(string? text) => Text = text;

        /// <summary>The value was changed from outside the box. Returns true if that replaced the
        /// element, in which case a focused box has lost focus.</summary>
        public bool Set(string? text)
        {
            if (string.Equals(text ?? "", Text ?? "", StringComparison.Ordinal)) return false;
            Text = text; Rendered = text; Key++;
            return true;
        }

        /// <summary>The element is about to be created anew (its container was not rendered for a
        /// while): show the current text rather than the value it was first created with.</summary>
        public void Sync() => Rendered = Text;
    }
}
