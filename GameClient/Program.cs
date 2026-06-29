using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Configuration;


// Test test = new Test();
// await test.putEventStart("IpAddress", "Port", "GameSessionId", DateTime.UtcNow.ToString("g"));
// Console.WriteLine("Success put event.");
// System.Environment.Exit(0);

Console.WriteLine("Start game client!");

String IP = "";
int PORT = 0;
String PlayerSessionId = "";
bool IsManualMatch = false;
String PlayerName = "";
String PlayerPass = "";
int PlayCount = 0;
String ClientsRegionCode = "";

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

PlayerName = Guid.NewGuid().ToString() + "-" + getRandomFruit() + "-" + getRandomString();
IP = config["AppSettings:IP"];
PORT = int.Parse(config["AppSettings:PORT"]);
PlayerSessionId = config["AppSettings:PlayerSessionId"];
IsManualMatch = bool.Parse(config["AppSettings:IsManualMatch"]);
ClientsRegionCode = config["AppSettings:ClientsRegionCode"];

Console.WriteLine("PlayerName: " + PlayerName);
Console.WriteLine("IP: " + IP);
Console.WriteLine("PORT: " + PORT);
Console.WriteLine("PlayerSessionId: " + PlayerSessionId);
Console.WriteLine("IsManualMatch: " + IsManualMatch);
Console.WriteLine("ClientsRegionCode: " + ClientsRegionCode);


if(!IsManualMatch)
{
    //Request matchmaking
    Console.WriteLine("Begin request matchmaking ");
    var restClient = new RestClient();
    var jsonDataMatchrequest = JsonSerializer.Serialize(new
    {
        PlayerName = PlayerName,
        PlayerPass = "test",
        RegionCode = ClientsRegionCode
    });

    String result = await restClient.PostDataAsync("https://00000000000.execute-api.ap-northeast-1.amazonaws.com/dev/matchrequest", jsonDataMatchrequest);
    RequestMatchmakingResult requestMatchmakingResult = JsonSerializer.Deserialize<RequestMatchmakingResult>(result);
    
    Console.WriteLine("TicketId: " + requestMatchmakingResult.TicketId);

    String ticketId = requestMatchmakingResult.TicketId;

    IP = "";
    PORT = 0;
    PlayerSessionId = "";
    //Describe matchmaking result
    for(int i = 0; i < 60; i++)
    {

        //Console.WriteLine("Begin request matchresult");

        var jsonDataMatchresult = JsonSerializer.Serialize(new
        {
            PlayerName = PlayerName
            ,PlayerPass = "test"
        });

        String resultMatchresult = await restClient.PostDataAsync("https://00000000000.execute-api.ap-northeast-1.amazonaws.com/dev/requestgamesession", jsonDataMatchresult);
        MatchmakingResult matchmakingResultMatchresult = JsonSerializer.Deserialize<MatchmakingResult>(resultMatchresult);
        Console.WriteLine("resultMatchresult: " + resultMatchresult);
        
        if (matchmakingResultMatchresult != null && !String.IsNullOrEmpty(matchmakingResultMatchresult.PlayerSessionId))
        {
            IP = matchmakingResultMatchresult.ip;
            PORT = matchmakingResultMatchresult.port;
            PlayerSessionId = matchmakingResultMatchresult.PlayerSessionId;

            Console.WriteLine("Assigned IP: " + IP);
            Console.WriteLine("Assigned PORT: " + PORT);
            break;
        }

        Console.WriteLine("Waiting for match...");
        Thread.Sleep(2000);
    }

    if(String.IsNullOrEmpty(IP) || PORT == 0 || String.IsNullOrEmpty(PlayerSessionId))
    {
        Console.WriteLine("Failed match making.");
        System.Environment.Exit(0);
    }
}


TcpClient tcpClient = new TcpClient();
IPAddress iPAddress = IPAddress.Parse(IP);
Console.WriteLine("Trying to connect server");
tcpClient.Connect(iPAddress, PORT);
var client = new ChatClient(tcpClient, getRandomFruit());

client.Send(client.getMsgCode(MsgCode.Ready) + PlayerSessionId);


Random rnd = new Random();
int loopCnt = 0;
int loopCntMax = 2;//5*360
while(true)
{
    Thread.Sleep(rnd.Next(3000, 4000));

    if(client.PlayStatus == PlayState.Waiting)
    {
        Console.WriteLine(client.ID + " ping");
        client.Send(client.getMsgCode(MsgCode.Ping));

        if(client.PlayerCount >= 2)
        {
            client.Send(client.getMsgCode(MsgCode.Ready) + PlayerSessionId);
        }
    } else if(client.PlayStatus == PlayState.Playing)
    {
        int myPlayCard = (loopCnt++ < loopCntMax) ? 1 : rnd.Next(1, 4);
        client.Send(client.getMsgCode(MsgCode.Play) + myPlayCard.ToString());
        Console.WriteLine("My card is -> " + myPlayCard + " (" + getCardName(myPlayCard) + ")");
    }
    else
    {
        Console.WriteLine("Exit");
        System.Environment.Exit(0);
    }
}


string getCardName(int card)
{
    switch (card)
    {
        case 1: return "Rock";
        case 2: return "Scissors";
        case 3: return "Paper";
        default: return "Unknown";
    }
}

string getRandomFruit()
{
    Random rnd = new Random();
    string[] fruits = new string[] { "apple", "mango", "papaya", "banana", "guava", "pineapple", "grape", "watermelon", "orange", 
    "peach", "cherry", "coconut", "apricot", "strawberry", "kiwi", "pear", "lemon", "lime", "plum", "jackfruit", "pomegranate" };
    return fruits[rnd.Next(0,fruits.Length)];
}

string getRandomString()
{
    // Creating object of random class 
    Random rand = new Random(); 
  
    // Choosing the size of string 
    // Using Next() string 
    int stringlen = 5; 
    int randValue; 
    string str = ""; 
    char letter; 
    for (int i = 0; i < stringlen; i++) 
    { 
  
        // Generating a random number. 
        randValue = rand.Next(0, 26); 
  
        // Generating random character by converting 
        // the random number into character. 
        letter = Convert.ToChar(randValue + 65); 
  
        // Appending the letter to string. 
        str = str + letter; 
    } 
    return str;
}