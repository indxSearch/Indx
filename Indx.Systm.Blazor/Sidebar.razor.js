// Sidebar's browser half: the shortcut that folds it to the rail (Cmd or Ctrl and the key, not
// while typing in a field), the viewer's kept choice (localStorage, which may be refused), and the
// drawer's keys on a narrow screen: Escape closes it, and opening it puts focus inside. The React
// Sidebar has the same.

const started = new Map();   // nav -> its teardown

const typing = (el) => el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName));

export function start(nav, dotnet, key, storageKey) {
    stop(nav);
    const onKey = (e) => {
        if (key && (e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === key && !typing(e.target)) {
            e.preventDefault();
            dotnet.invokeMethodAsync('Toggle').catch(() => { });
        } else if (e.key === 'Escape' && nav.classList.contains('indx-sidebar--open')) {
            e.preventDefault();
            dotnet.invokeMethodAsync('Close').catch(() => { });
        }
    };
    document.addEventListener('keydown', onKey);
    started.set(nav, () => document.removeEventListener('keydown', onKey));
    let kept = null;
    if (storageKey) {
        try {
            const v = localStorage.getItem(storageKey);
            if (v === 'rail') kept = true; else if (v === 'full') kept = false;
        } catch { }
    }
    return { mac: isMac(), kept };
}

export function stop(nav) {
    started.get(nav)?.();
    started.delete(nav);
}

// Kept twice: in localStorage, read here, and in a cookie of the same name, which a server-rendered
// page can read to draw the rail from its first frame (the console's MainLayout does).
export function keep(storageKey, rail) {
    try { localStorage.setItem(storageKey, rail ? 'rail' : 'full'); } catch { }
    try { document.cookie = `${encodeURIComponent(storageKey)}=${rail ? 'rail' : 'full'}; path=/; max-age=31536000; samesite=lax`; } catch { }
}

// The drawer opened: focus its open item, or its first.
export function drawer(nav, open) {
    if (!open || !nav) return;
    requestAnimationFrame(() => {
        const target = nav.querySelector('[aria-current="page"]') || nav.querySelector('.indx-sidebar-body a, .indx-sidebar-body button');
        target?.focus();
    });
}

function isMac() {
    return /Mac|iPhone|iPad|iPod/.test(navigator.platform) || /Mac OS X/.test(navigator.userAgent);
}
