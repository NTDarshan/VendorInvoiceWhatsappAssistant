using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using System.ComponentModel.DataAnnotations;

namespace VendorInvoiceAssistant.Data
{
    public enum ConversationState
    {
        Idle,
        AwaitingMenuSelection,
        AwaitingInvoiceSelection,
        AwaitingInvoiceNumber
    }

    public class ConversationContext
    {
        [Key]
        public string PhoneNumber { get; set; } = "";

        public string? LastInvoiceNumber { get; set; }

        public string? SelectedMenuOption { get; set; }

        public ConversationState State { get; set; } = ConversationState.Idle;

        public DateTime LastUpdated { get; set; }
    }
}