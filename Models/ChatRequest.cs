using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VendorInvoiceAssistant.Models
{
    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;

        public string PhoneNumber { get; set; } = string.Empty;
    }
}