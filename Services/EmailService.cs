using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Caching.Memory;
using MimeKit;

namespace VendorInvoiceAssistant.Services;

public class EmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;
    private readonly IMemoryCache _cache;

    // Throttle: one reminder per approver per invoice per 4 hours
    private static readonly TimeSpan ReminderCooldown = TimeSpan.FromHours(4);

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger, IMemoryCache cache)
    {
        _configuration = configuration;
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Sends an approval reminder to the given approver.
    /// Returns true if the email was sent, false if throttled or if sending failed.
    /// </summary>
    public async Task<bool> SendApprovalReminderAsync(
        string approverName,
        string approverEmail,
        string invoiceNumber,
        string vendorName,
        decimal invoiceAmount,
        string currencyCode,
        DateTime invoiceDate,
        DateTime? dueDate)
    {
        _logger.LogInformation("[Email] SendApprovalReminderAsync called — Invoice: {Invoice}, Approver: {Approver} <{Email}>",
            invoiceNumber, approverName, approverEmail);

        // ── Throttle check ────────────────────────────────────────────────────
        var cacheKey = $"email_sent:{invoiceNumber}:{approverEmail}";
        if (_cache.TryGetValue(cacheKey, out _))
        {
            _logger.LogInformation("[Email] Throttled — reminder already sent to {Email} for {Invoice} within the last {Hours}h",
                approverEmail, invoiceNumber, ReminderCooldown.TotalHours);
            return false;
        }

        // ── Config validation ─────────────────────────────────────────────────
        var host      = _configuration["Smtp:Host"];
        var portStr   = _configuration["Smtp:Port"] ?? "587";
        var username  = _configuration["Smtp:Username"];
        var password  = _configuration["Smtp:Password"];
        var fromName  = _configuration["Smtp:FromName"] ?? "Vendor Invoice Assistant";
        var fromEmail = _configuration["Smtp:FromEmail"] ?? username;

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogError("[Email] SMTP configuration is incomplete — Host: '{Host}', Username: '{User}', Password set: {PwdSet}. " +
                             "Update Smtp section in appsettings.json.",
                host ?? "(null)", username ?? "(null)", !string.IsNullOrWhiteSpace(password));
            return false;
        }

        if (!int.TryParse(portStr, out var port))
        {
            _logger.LogError("[Email] Smtp:Port '{PortStr}' is not a valid integer", portStr);
            return false;
        }

        _logger.LogInformation("[Email] SMTP config — Host: {Host}, Port: {Port}, Username: {User}, From: {From}",
            host, port, username, fromEmail);

        // ── Build message ─────────────────────────────────────────────────────
        var dueDateLine = dueDate.HasValue
            ? $"Due Date       : {dueDate.Value:dd MMM yyyy}"
            : "Due Date       : Not specified";

        var subject = $"Action Required: Invoice {invoiceNumber} Pending Your Approval";

        var body = $"""
            Dear {approverName},

            This is an automated reminder that the following vendor invoice is awaiting your approval action:

              Invoice Number : {invoiceNumber}
              Vendor         : {vendorName}
              Invoice Amount : {currencyCode} {invoiceAmount:N2}
              Invoice Date   : {invoiceDate:dd MMM yyyy}
              {dueDateLine}

            The vendor has contacted us regarding the status of this invoice. Please review and take the necessary action at your earliest convenience to avoid payment delays.

            If you have already approved this invoice, please disregard this message.

            Thank you,
            Vendor Invoice Assistant
            Powered by Acronotics
            """;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromEmail!));
        message.To.Add(new MailboxAddress(approverName, approverEmail));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        // ── Send via MailKit (proper STARTTLS) ────────────────────────────────
        try
        {
            _logger.LogInformation("[Email] Connecting to SMTP {Host}:{Port} using STARTTLS", host, port);

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(host, port, SecureSocketOptions.StartTls);

            _logger.LogInformation("[Email] Connected. Authenticating as {User}", username);
            await smtp.AuthenticateAsync(username, password);

            _logger.LogInformation("[Email] Authenticated. Sending to {To}", approverEmail);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _cache.Set(cacheKey, true, ReminderCooldown);
            _logger.LogInformation("[Email] SUCCESS — Reminder sent to {Approver} ({Email}) for invoice {Invoice}",
                approverName, approverEmail, invoiceNumber);
            return true;
        }
        catch (AuthenticationException ex)
        {
            _logger.LogError(ex,
                "[Email] Authentication failed for {User}. " +
                "For Gmail: ensure you are using an App Password (Google Account → Security → 2-Step Verification → App passwords), not your regular password.",
                username);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Email] Failed to send to {Email} for invoice {Invoice}", approverEmail, invoiceNumber);
            return false;
        }
    }
}
