using Azure;
using Azure.Communication.Email;
using Microsoft.AspNetCore.Identity.UI.Services;

namespace IndxServer.Services
{
    /// <summary>
    /// Email sender using Azure Communication Services
    /// </summary>
    public class AzureCommunicationEmailSender : IEmailSender
    {
        private readonly EmailClient _emailClient;
        private readonly string _fromAddress;
        private readonly string _fromName;
        private readonly ILogger<AzureCommunicationEmailSender> _logger;

        /// <summary>
        /// Initializes a new instance of the AzureCommunicationEmailSender
        /// </summary>
        /// <param name="configuration">Application configuration containing connection string</param>
        /// <param name="logger">Logger instance</param>
        public AzureCommunicationEmailSender(
            IConfiguration configuration,
            ILogger<AzureCommunicationEmailSender> logger)
        {
            var connectionString = configuration["Email:AzureCommunicationServices:ConnectionString"];
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("Azure Communication Services connection string is not configured");
            }

            _emailClient = new EmailClient(connectionString);
            _fromAddress = configuration["Email:FromAddress"] ?? "noreply@indx.co";
            _fromName = configuration["Email:FromName"] ?? "Indx Authentication";
            _logger = logger;
        }

        /// <summary>
        /// Sends an email using Azure Communication Services
        /// </summary>
        /// <param name="email">Recipient email address</param>
        /// <param name="subject">Email subject</param>
        /// <param name="htmlMessage">Email body (HTML format)</param>
        public async Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            try
            {
                var emailMessage = new EmailMessage(
                    senderAddress: _fromAddress,
                    recipientAddress: email,
                    content: new EmailContent(subject)
                    {
                        PlainText = StripHtml(htmlMessage),
                        Html = htmlMessage
                    });

                EmailSendOperation emailSendOperation = await _emailClient.SendAsync(
                    WaitUntil.Started,
                    emailMessage);

                _logger.LogInformation("Email sent to {Email} with message ID: {MessageId}",
                    email, emailSendOperation.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {Email}", email);
                throw;
            }
        }

        private static string StripHtml(string html)
        {
            // Simple HTML stripping for plain text version
            return System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);
        }
    }
}
