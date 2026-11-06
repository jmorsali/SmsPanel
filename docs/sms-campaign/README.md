# SMS campaigns and inbound replies

This domain covers merchant imports, personalized campaign preparation, status-based recipient selection, explicit concurrent dispatch, polling inbound replies, tolerant but checksum-safe IBAN correction, automated replies, campaign-scoped reporting, and reconciliation.

- [Campaign lifecycle](campaign-lifecycle.md)
- [Inbound reply processing](inbound-replies.md)
- The inbound flow also covers operator-assisted card-to-IBAN conversion, linked-person resolution, and account-holder name review.
- [Merchant import and person-name normalization](merchant-import.md)

## Current implementation

Primary sources:

- `SmsPanel.Api/Program.cs`
- `SmsPanel.Api/Services/Workers.cs`
- `SmsPanel.Api/Services/FarapayamakClient.cs`
- `SmsPanel.Api/Services/InboundMessageProcessor.cs`
- `SmsPanel.Api/Services/MessageTemplateRenderer.cs`
- `SmsPanel.Api/Services/NormalizationServices.cs`
- `SmsPanel.Api/Services/IbanOwnershipVerificationService.cs`
- `SmsPanel.Api/Services/InboundPersonNameResolver.cs`
- `SmsPanel.Api/Services/PersianPersonNameParser.cs`
- `SmsPanel.Api/Services/MerchantImportWorker.cs`
- `SmsPanel.Web/src/App.tsx`

Automated checks:

- `SmsPanel.Tests/MessagingTests.cs`
- `SmsPanel.Tests/NormalizationTests.cs`
- `SmsPanel.Tests/PersonNameMatcherTests.cs`
