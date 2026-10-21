using Microsoft.AspNetCore.Components;

namespace IndxServer.Components.Datasets
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

        /// <summary>For the Observr client, which is absent where the service is not configured.</summary>
        [Inject] protected IServiceProvider TabServices { get; set; } = default!;

        private bool _subscribed;
        private IndxServer.Services.Observr.ObservrClient? _observr;

        protected override void OnInitialized()
        {
            Ctx.Changed += OnContextChanged;
            _subscribed = true;
            _observr = TabServices.GetService(typeof(IndxServer.Services.Observr.ObservrClient)) as IndxServer.Services.Observr.ObservrClient;
            if (_observr != null) _observr.Applied += OnObservrAppliedEvent;
        }

        private void OnObservrAppliedEvent(IndxServer.Services.Observr.ObservrApplied applied)
        {
            if (applied.TeamId != Ctx.TeamId || applied.DataSetName != Ctx.Name) return;
            _ = InvokeAsync(() =>
            {
                OnObservrApplied(applied.Kinds);
                StateHasChanged();
                Ctx.NotifyChanged();
            });
        }

        /// <summary>A yes to Observr changed this dataset's saved settings, wherever it was said (the
        /// Agent panel, the Observr menu, the Status tab). A tab reloads what it shows of those kinds,
        /// or, while it holds unsaved edits, keeps them and says the saved values changed. Called on
        /// the render thread; every tab hears it, open or not, since an inactive one keeps its state.</summary>
        protected virtual void OnObservrApplied(IReadOnlySet<string> kinds) { }

        private void OnContextChanged()
        {
            // Inactive tabs render nothing, so this is cheap for them too; always re-render
            // rather than risk a stale first render surviving until the next parameter change.
            _ = InvokeAsync(StateHasChanged);
        }

        /// <summary>Re-renders this tab and the rest of the panel from any thread.</summary>
        protected Task Render() => InvokeAsync(() => { StateHasChanged(); Ctx.NotifyChanged(); });

        public virtual void Dispose()
        {
            if (_subscribed) Ctx.Changed -= OnContextChanged;
            if (_observr != null) _observr.Applied -= OnObservrAppliedEvent;
        }
    }
}
