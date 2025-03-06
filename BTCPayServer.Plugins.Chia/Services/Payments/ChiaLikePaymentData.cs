using BTCPayServer.Client.Models;

namespace BTCPayServer.Plugins.Chia.Services.Payments
{
    public class ChiaLikePaymentData
    {
        public uint ConfirmationCount { get; set; }
        public required string TransactionId { get; init; }
        public uint BlockHeight { get; init; }
        public required string To { get; init; } // For future usages
        public required string From { get; init; } // For future usages

        
 
        public bool PaymentConfirmed(SpeedPolicy speedPolicy)
        {
            return speedPolicy switch
            {
                SpeedPolicy.HighSpeed => ConfirmationCount >= 5,
                SpeedPolicy.MediumSpeed => ConfirmationCount >= 10,
                SpeedPolicy.LowMediumSpeed => ConfirmationCount >= 20,
                SpeedPolicy.LowSpeed => ConfirmationCount >= 30,
                _ => false
            };
        }
    }
}
