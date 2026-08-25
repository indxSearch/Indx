// Reports the chart container's width to .NET so the SVG can be sized in real pixels (the geometry
// is computed in C#). A ResizeObserver keeps it in sync as the container resizes.

export function observe(el, dotNetRef) {
    if (!el) return;
    const report = () => dotNetRef.invokeMethodAsync('OnResize', el.clientWidth);
    const ro = new ResizeObserver(report);
    el._indxChartRO = ro;
    ro.observe(el);
    report();
}

export function unobserve(el) {
    if (!el) return;
    if (el._indxChartRO) el._indxChartRO.disconnect();
    delete el._indxChartRO;
}
