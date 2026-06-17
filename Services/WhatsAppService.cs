using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VendorInvoiceAssistant.Services;

public class WhatsAppService
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ILogger<WhatsAppService> _logger;

    public WhatsAppService(IConfiguration configuration, HttpClient httpClient, ILogger<WhatsAppService> logger)
    {
        _configuration = configuration;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task SendMessage(string phoneNumber, string message)
    {
        var token = _configuration["WhatsApp:AccessToken"];
        var phoneNumberId = _configuration["WhatsApp:PhoneNumberId"];
        var url = $"https://graph.facebook.com/v23.0/{phoneNumberId}/messages";

        var payload = new
        {
            messaging_product = "whatsapp",
            to = phoneNumber,
            type = "text",
            text = new { body = message }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("SendMessage failed {StatusCode}: {Body}", (int)response.StatusCode, body);
        }
    }

    public async Task SendInteractiveList(string phoneNumber)
    {
        var token = _configuration["WhatsApp:AccessToken"];
        var phoneNumberId = _configuration["WhatsApp:PhoneNumberId"];
        var url = $"https://graph.facebook.com/v23.0/{phoneNumberId}/messages";

        var payload = new
        {
            messaging_product = "whatsapp",
            to = phoneNumber,
            type = "interactive",
            interactive = new
            {
                type = "list",
                header = new { type = "text", text = "Hello, Infosys BPM! How can I help you today?" },
                body = new { text = "Please choose an option:" },
                footer = new { text = "Powered by Acronotics" },
                action = new
                {
                    button = "View Options",
                    sections = new[]
                    {
                        new
                        {
                            title = "Invoice Management",
                            rows = new[]
                            {
                                new { id = "invoice_status", title = "Invoice Status", description = "Check the status of an invoice" },
                                new { id = "payment_date", title = "Payment Date", description = "Get payment date details" },
                                new { id = "outstanding_invoices", title = "My Outstanding Invoices", description = "View all pending invoices" },
                                new { id = "rejected_invoices", title = "Rejected/On-Hold", description = "Check rejected or on-hold invoices" },
                                new { id = "approval_status", title = "Approval Status", description = "Track invoice approvals" }
                            }
                        },
                        new
                        {
                            title = "Support & Assistance",
                            rows = new[]
                            {
                                new { id = "raise_dispute", title = "Raise a Dispute / Query", description = "Report an issue or query" },
                                new { id = "talk_to_ap", title = "Talk to AP Team", description = "Connect with Accounts Payable" },
                                new { id = "other", title = "Other", description = "Ask any other question" }
                            }
                        }
                    }
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("SendInteractiveList failed {StatusCode}: {Body}", (int)response.StatusCode, body);
        }
    }

    public async Task SendInvoiceSelectionList(string phoneNumber, List<Data.Invoice> invoices)
    {
        var token = _configuration["WhatsApp:AccessToken"];
        var phoneNumberId = _configuration["WhatsApp:PhoneNumberId"];
        var url = $"https://graph.facebook.com/v23.0/{phoneNumberId}/messages";

        // 9 invoice rows + 1 "Other" = 10 max per section
        var invoiceRows = invoices.Take(9).Select(inv =>
        {
            var title = inv.InvoiceNumber.Length > 24 ? inv.InvoiceNumber[..24] : inv.InvoiceNumber;
            return new { id = inv.InvoiceNumber, title, description = "" };
        });
        var rows = invoiceRows.Append(new { id = "other", title = "Other", description = "" }).ToArray();

        var payload = new
        {
            messaging_product = "whatsapp",
            to = phoneNumber,
            type = "interactive",
            interactive = new
            {
                type = "list",
                header = new { type = "text", text = "Select an Invoice" },
                body = new { text = "Here are your invoices. Tap one to get details." },
                footer = new { text = "Powered by Acronotics" },
                action = new
                {
                    button = "View Invoices",
                    sections = new[]
                    {
                        new { title = "Your Invoices", rows }
                    }
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("SendInvoiceSelectionList failed {StatusCode}: {Body}", (int)response.StatusCode, body);
        }
    }
}