using Microsoft.AspNetCore.Components;

namespace IndxCloudApi.Components.Datasets
{
    /// <summary>
    /// Base for the tab components under <see cref="DatasetPanel"/>: receives the cascaded
    /// <see cref="DatasetContext"/>, re-renders whenever any tab or the panel raises
    /// <see cref="DatasetContext.Changed"/>, and renders nothing while not <see cref="Active"/>.
    /// A shared field-config edit made in one tab is therefore visible in every other at once.
    /// </summary>
    public abstract class DatasetTabBase : ComponentBase, IDisposable
    {
        [CascadingParameter] public DatasetContext Ctx { get; set; } = default!;

        /// <summary>Only the active tab renders; edit state and in-flight work persist while inactive.</summary>
        [Parameter] public bool Active { get; set; }

        private bool _subscribed;

        protected override void OnInitialized()
        {
            Ctx.Changed += OnContextChanged;
            _subscribed = true;
        }

        private void OnContextChanged()
        {
            if (Active) _ = InvokeAsync(StateHasChanged);
        }

        /// <summary>Re-renders this tab and the rest of the panel from any thread.</summary>
        protected Task Render() => InvokeAsync(() => { StateHasChanged(); Ctx.NotifyChanged(); });

        public virtual void Dispose()
        {
            if (_subscribed) Ctx.Changed -= OnContextChanged;
        }
    }
}
