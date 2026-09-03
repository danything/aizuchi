using System.Text.Json;
using Aizuchi.Slack;

public class SlackJsonTests
{
    [Fact]
    public void Socket_Modeの封筒を読める()
    {
        const string json = """
            {"envelope_id":"env-1","type":"events_api","accepts_response_payload":false,
             "payload":{"type":"event_callback","event_id":"Ev1",
               "event":{"type":"message","channel":"C1","channel_type":"im","user":"U1",
                        "text":"hello","ts":"1.000","thread_ts":"0.900"}}}
            """;
        var env = JsonSerializer.Deserialize(json, SlackJson.Default.Envelope)!;
        Assert.Equal("events_api", env.Type);
        Assert.Equal("env-1", env.EnvelopeId);
        Assert.Equal("Ev1", env.Payload!.EventId);
        var ev = env.Payload.Event!;
        Assert.Equal("im", ev.ChannelType);
        Assert.Equal("0.900", ev.ThreadTs);
        Assert.Equal("hello", ev.Text);
    }

    [Fact]
    public void disconnectの理由を読める()
    {
        var env = JsonSerializer.Deserialize("""{"type":"disconnect","reason":"refresh_requested"}""", SlackJson.Default.Envelope)!;
        Assert.Equal("disconnect", env.Type);
        Assert.Equal("refresh_requested", env.Reason);
        Assert.Null(env.EnvelopeId);
    }

    [Fact]
    public void ackとpostMessageはsnake_caseでnullを省く()
    {
        Assert.Equal("""{"envelope_id":"x"}""", JsonSerializer.Serialize(new Ack { EnvelopeId = "x" }, SlackJson.Default.Ack));
        var post = JsonSerializer.Serialize(new PostMessageRequest { Channel = "C1", Text = "t" }, SlackJson.Default.PostMessageRequest);
        Assert.Equal("""{"channel":"C1","text":"t"}""", post);
        var threaded = JsonSerializer.Serialize(new PostMessageRequest { Channel = "C1", Text = "t", ThreadTs = "1.0" }, SlackJson.Default.PostMessageRequest);
        Assert.Contains("\"thread_ts\":\"1.0\"", threaded);
    }
}
