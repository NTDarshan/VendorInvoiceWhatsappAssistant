using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using VendorInvoiceAssistant.Data;

namespace VendorInvoiceAssistant
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();
            builder.Services.AddScoped<VendorInvoiceAssistant.Services.AiService>();
            builder.Services.AddScoped<VendorInvoiceAssistant.Services.InvoiceService>();
            builder.Services.AddScoped<VendorInvoiceAssistant.Services.ConversationHistoryService>();
            builder.Services.AddScoped<VendorInvoiceAssistant.Services.VendorAgentService>();
            builder.Services.AddScoped<VendorInvoiceAssistant.Services.ChatService>();
            builder.Services.AddHttpClient<VendorInvoiceAssistant.Services.WhatsAppService>();
            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

            builder.Services.AddOpenApi(options =>
            {
                options.AddDocumentTransformer((document, context, cancellationToken) =>
                {
                    document.Info.Title = "Vendor Invoice Assistant API";
                    document.Info.Version = "v1";
                    document.Info.Description = "API for managing vendor invoices with AI-powered chat and WhatsApp integration.";
                    return Task.CompletedTask;
                });
            });

            var app = builder.Build();

            app.MapOpenApi();
            app.MapScalarApiReference(options =>
            {
                options.Title = "Vendor Invoice Assistant";
                options.Theme = ScalarTheme.DeepSpace;
            });

            app.UseHttpsRedirection();

            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}
