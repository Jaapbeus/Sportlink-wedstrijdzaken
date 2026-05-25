window.themeHelper = {
    apply: function (primary, secondary, accent, textOnPrimary) {
        var root = document.documentElement;
        root.style.setProperty('--theme-primary',         primary);
        root.style.setProperty('--theme-secondary',       secondary);
        root.style.setProperty('--theme-accent',          accent);
        root.style.setProperty('--theme-text-on-primary', textOnPrimary);
    }
};
