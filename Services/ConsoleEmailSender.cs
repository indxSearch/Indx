using Microsoft.AspNetCore.Identity.UI.Services;

namespace IndxServer.Services
{
    /// <summary>
    /// Email sender that logs to console - for development/testing
    /// </summary>
    public class ConsoleEmailSender : IEmailSender
    {
        private readonly ILogger<ConsoleEmailSender> _logger;

        /// <summary>
        /// Initializes a new instance of the ConsoleEmailSender
        /// </summary>
        /// <param name="logger">Logger instance for console output</param>
        public ConsoleEmailSender(ILogger<ConsoleEmailSender> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Logs email details to console instead of sending actual email (for development/testing)
        /// </summary>
        /// <param name="email">Recipient email address</param>
        /// <param name="subject">Email subject</param>
        /// <param name="htmlMessage">Email body (HTML format)</param>
        public Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            _logger.LogInformation("=== EMAIL (Console Mode) ===");
            _logger.LogInformation("To: {Email}", email);
            _logger.LogInformation("Subject: {Subject}", subject);
            _logger.LogInformation("Body: {Body}", htmlMessage);
            _logger.LogInformation("============================");

            // In console mode, we just log - no actual email sent
            return Task.CompletedTask;
        }
    }
}
