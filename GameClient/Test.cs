using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using System.Text.Json;

public class Test
{
    public async Task putEventStart(String IpAddress, String Port, String GameSessionId, String UtcTime)
    {
        var eventDetail = new
        {
            IpAddress = IpAddress,
            Port = Port,
            GameSessionId = GameSessionId,
            UtcTime = UtcTime
        };

        var eventBridgeClient = new AmazonEventBridgeClient();
        var response = await eventBridgeClient.PutEventsAsync(
            new PutEventsRequest()
            {
                Entries = new List<PutEventsRequestEntry>()
                {
                    new PutEventsRequestEntry()
                    {
                        Source = "game_server",
                        Detail = JsonSerializer.Serialize(eventDetail),
                        DetailType = "start",
                        EventBusName = "GameServer"
                    }
                }
            });

        Console.WriteLine($"Successfully sent {response.Entries.Count} event(s)");
        Console.WriteLine("response.FailedEntryCount: " + response.FailedEntryCount.ToString());
        if(response.FailedEntryCount > 0)
        {
            writeLog("Failed to send event to EventBridge");
            writeLog(response.Entries[0].ErrorMessage);
        }
    }

    public void writeLog(String log)
    {
        Console.WriteLine(log);
    }
}