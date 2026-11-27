// The user's appearance, applied the moment it changes on the account page; on every page load
// App.razor renders the same attributes on <html> (Services/Appearance.cs decides them). A null
// removes the attribute.
window.indxAppearance = {
    apply(theme, cursors) {
        const root = document.documentElement;
        const set = (name, value) => value ? root.setAttribute(name, value) : root.removeAttribute(name);
        set('data-theme', theme);
        set('data-cursors', cursors);
    },
};
