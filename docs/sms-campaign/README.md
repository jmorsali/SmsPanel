# SMS campaigns and inbound replies

This domain covers merchant imports, personalized campaign preparation, explicit dispatch, polling inbound replies, IBAN validation, and reconciliation.

- [Campaign lifecycle](campaign-lifecycle.md)
- [Inbound reply processing](inbound-replies.md)

## Current implementation

Primary sources:

- `SmsPanel.Api/Program.cs`
- `SmsPanel.Api/Services/Workers.cs`
- `SmsPanel.Api/Services/FarapayamakClient.cs`
- `SmsPanel.Api/Services/InboundMessageProcessor.cs`
- `SmsPanel.Api/Services/MessageTemplateRenderer.cs`
- `SmsPanel.Web/src/App.tsx`

Automated checks:

- `SmsPanel.Tests/MessagingTests.cs`
- `SmsPanel.Tests/NormalizationTests.cs`
