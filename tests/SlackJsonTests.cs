using System.Text.Json;
using Aizuchi.Slack;
using System.Threading.Tasks;

public class SlackJsonTests
{
    [Test]
    public async Task Socket_Modeの封筒を読める()
    {
        const string json = """
            {"envelope_id":"env-1","type":"events_api","accepts_response_payload":false,
             "payload":{"type":"event_callback","event_id":"Ev1",
               "event":{"type":"message","channel":"C1","channel_type":"im","user":"U1",
                        "text":"hello","ts":"1.000","thread_ts":"0.900"}}}
            """;
        var env = JsonSerializer.Deserialize(json, SlackJson.Default.Envelope)!;
        await Assert.That(env.Type).IsEqualTo("events_api");
        await Assert.That(env.EnvelopeId).IsEqualTo("env-1");
        await Assert.That(env.Payload!.EventId).IsEqualTo("Ev1");
        var ev = env.Payload.Event!;
        await Assert.That(ev.ChannelType).IsEqualTo("im");
        await Assert.That(ev.ThreadTs).IsEqualTo("0.900");
        await Assert.That(ev.Text).IsEqualTo("hello");
    }

    [Test]
    public async Task disconnectの理由を読める()
    {
        var env = JsonSerializer.Deserialize("""{"type":"disconnect","reason":"refresh_requested"}""", SlackJson.Default.Envelope)!;
        await Assert.That(env.Type).IsEqualTo("disconnect");
        await Assert.That(env.Reason).IsEqualTo("refresh_requested");
        await Assert.That(env.EnvelopeId).IsNull();
    }

    [Test]
    public async Task ackとpostMessageはsnake_caseでnullを省く()
    {
        await Assert.That(JsonSerializer.Serialize(new Ack { EnvelopeId = "x" }, SlackJson.Default.Ack)).IsEqualTo("""{"envelope_id":"x"}""");
        var post = JsonSerializer.Serialize(new PostMessageRequest { Channel = "C1", Text = "t" }, SlackJson.Default.PostMessageRequest);
        await Assert.That(post).IsEqualTo("""{"channel":"C1","text":"t"}""");
        var threaded = JsonSerializer.Serialize(new PostMessageRequest { Channel = "C1", Text = "t", ThreadTs = "1.0" }, SlackJson.Default.PostMessageRequest);
        await Assert.That(threaded).Contains("\"thread_ts\":\"1.0\"");
    }
}