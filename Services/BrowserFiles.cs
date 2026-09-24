using Microsoft.JSInterop;

namespace IndxServer.Services
{
    /// <summary>
    /// The browser-side file and clipboard helpers the pages need (backed by
    /// wwwroot/js/browser-files.js). Scoped so it lives with the circuit like IJSRuntime.
    /// </summary>
    public class BrowserFiles(IJSRuntime js)
    {
        /// <summary>Offers <paramref name="content"/> as a download named <paramref name="fileName"/>.</summary>
        public ValueTask DownloadAsync(string fileName, string content, string contentType = "application/json")
            => js.InvokeVoidAsync("indxDownloadFile", fileName, content, contentType);

        /// <summary>Clicks a hidden <c>&lt;input type="file"&gt;</c> so a styled button can open the picker.</summary>
        public ValueTask TriggerFileInputAsync(string elementId)
            => js.InvokeVoidAsync("indxTriggerFileInput", elementId);

        /// <summary>
        /// Arms or disarms the browser's own "Leave site?" prompt. Covers closing the tab,
        /// reloading and typing a new address, none of which reach Blazor's own navigation
        /// handler. In-app navigation is guarded separately, with a real dialog.
        /// </summary>
        public ValueTask SetLeaveWarningAsync(bool on)
            => js.InvokeVoidAsync("indxSetLeaveWarning", on);

        public ValueTask CopyToClipboardAsync(string text)
            => js.InvokeVoidAsync("copyToClipboard", text);
    }
}
