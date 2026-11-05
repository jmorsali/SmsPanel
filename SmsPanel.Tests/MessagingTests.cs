using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmsPanel.Api.Domain;
using SmsPanel.Api.Services;

namespace SmsPanel.Tests;

public sealed class MessagingTests
{
    [Fact]
    public void Template_renderer_supports_full_name_bank_and_received_iban_suffix()
    {
        var renderer = new MessageTemplateRenderer();
        var person = new Person { NationalCode = "0012345678", FirstName = "علی", LastName = "احمدی", Mobile = "09120000000" };

        Assert.Equal("علی احمدی - پارسیان", renderer.Render("{{نام_کامل}} - {{بانک_فعلی}}", person, "پارسیان"));
        Assert.Equal("پایان 9002", renderer.RenderConfirmation("پایان {{چهار_رقم_آخر_شبای_دریافتی}}", person, "IR820540102680020817909002"));
        Assert.Equal("علی: شماره شبا نامعتبر است", renderer.RenderInvalidReply("{{نام}}: {{دلیل_نامعتبر_بودن}}", person, "شماره شبا نامعتبر است"));
        Assert.Empty(renderer.GetUnknownInvalidReplyFields("{{نام}}: {{دلیل_نامعتبر_بودن}}"));
    }

    [Fact]
    public async Task Send_uses_form_encoding_expected_by_provider()
    {
        var handler = new RecordingHandler("{\"Value\":\"123\",\"RetStatus\":1,\"StrRetStatus\":\"Ok\"}");
        var client = CreateClient(handler);

        var result = await client.SendAsync(["09120000000"], "پیام آزمایشی", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("application/x-www-form-urlencoded", handler.ContentType);
        Assert.Contains("isFlash=False", handler.Body);
        Assert.Contains("to=09120000000", handler.Body);
    }

    [Fact]
    public async Task Inbox_reads_current_envelope_and_message_fields()
    {
        const string json = """
        {"MyBase":{"Value":"Ok","RetStatus":1,"StrRetStatus":"Ok"},"Data":[{"MsgID":42,"Body":"IR820540102680020817909002","SendDate":"2026-11-03T12:00:00+03:00","Sender":"09120000000","Receiver":"20000000"}]}
        """;
        var handler = new RecordingHandler(json);
        var client = CreateClient(handler);

        var messages = await client.GetMessagesAsync(0, 100, CancellationToken.None);

        var message = Assert.Single(messages);
        Assert.Equal("42", message.Id);
        Assert.Equal("09120000000", message.Sender);
        Assert.Equal("application/json", handler.ContentType);
    }

    private static FarapayamakClient CreateClient(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new FarapayamakOptions { Username = "synthetic-user", Password = "synthetic-password", FromNumber = "20000000" }),
        NullLogger<FarapayamakClient>.Instance,
        new InboundTextDecoder());

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string? ContentType { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }
}
