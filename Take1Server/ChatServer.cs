using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using Amazon;
// using Amazon.GameLift;
// using Aws.GameLift;
// using Aws.GameLift.Server;
// using Aws.GameLift.Server.Model;
using Amazon.CloudWatchLogs;
// using Amazon.GameLift.Model;

class ChatServer
{
    static int port = 7777;
    private const int MaxPlayers = 2;
    private TcpListener listener;
    private bool stopping;
    private static ConcurrentDictionary<string, ChatClient> clients = new ConcurrentDictionary<string, ChatClient>();

    // Guards the "check capacity then admit" sequence so it stays atomic against
    // receiver threads that remove clients on disconnect.
    private readonly object admissionLock = new object();

    Dictionary<String, int> cards;

    AWSCloudWatch acw = new AWSCloudWatch();

    private readonly AgonesManager agones;

    public ChatServer()
    {
        listener = new TcpListener(IPAddress.Any, port);
        stopping = false;
        cards = new Dictionary<String, int>();
        writeLog("Server is starting on port " + port);
        acw = new AWSCloudWatch();
        agones = new AgonesManager(writeLog);
    }

    public void Start()
    {
        // /*
        // Initializes the Amazon GameLift SDK for a managed EC2 fleet. 
        // Call this method on launch, before any other initialization related to Amazon GameLift occurs.
        // This method reads server parameters from the host environment to set up communication between the server and the Amazon GameLift service.
        // */

        // //Define the server parameters
        // string webSocketUrl = "wss://ap-northeast-1.api.amazongamelift.com";
        // string processId = "PID1234";
        // string fleetId = "fleet-00000000-0000-0000-0000-000000000000";
        // string hostId = "example-compute";
        // string authToken = "00000000-0000-0000-0000-000000000000";
        // ServerParameters serverParameters = new ServerParameters(webSocketUrl, processId, hostId, fleetId, authToken);

        // GenericOutcome initSDKOutcome = GameLiftServerAPI.InitSDK(serverParameters);
        // if(initSDKOutcome.Success == false)
        // {
        //     writeLog("Server failed to use the AWS GameLift SDK - InitSDK");
        //     writeLog(initSDKOutcome.Error.ToString());
        //     System.Threading.Thread.Sleep(2000);
        //     System.Environment.Exit(1);
        // }


        // // Set parameters and call ProcessReady
        // ProcessParameters processParams = new ProcessParameters(
        // this.OnStartGameSession,
        // this.OnProcessTerminate,
        // this.OnHealthCheck,
        // this.OnUpdateGameSession,
        // port,
        // new LogParameters(new List<string>()  
        // // Examples of log and error files written by the game server
        // {
        //     "./",
        //     "./"
        // })
        // );
        // GenericOutcome processReadyOutcome = GameLiftServerAPI.ProcessReady(processParams);
        // if(processReadyOutcome.Success == false)
        // {
        //     writeLog("Server failed to use the AWS GameLift SDK - ProcessReady");
        //     writeLog(processReadyOutcome.Error.ToString());
        //     System.Threading.Thread.Sleep(2000);
        //     System.Environment.Exit(1);
        // }

        // Tell the Agones sidecar this GameServer is up and can receive players.
        // Health pings are then sent automatically by the SDK.
        agones.Ready();

        listener.Start();
        writeLog("Server is listening on port " + port);
        while (!stopping)
        {
            TcpClient client = listener.AcceptTcpClient();
            if (client == null)
            {
                break;
            }

            // The remote endpoint can be null if the peer dropped immediately after accept.
            var remoteEndPoint = client.Client.RemoteEndPoint;
            if (remoteEndPoint == null)
            {
                writeLog("Accepted a client with no remote endpoint. Closing.");
                try { client.Close(); } catch { }
                continue;
            }

            string clientId = remoteEndPoint.ToString();
            Console.WriteLine(clientId + " has been connected.");
            ChatClient user = new ChatClient(client, clientId);

            // Atomically decide admission so we never exceed MaxPlayers, even when a
            // slot is freed concurrently by a disconnecting player.
            bool admitted;
            int currentCount;
            lock (admissionLock)
            {
                if (clients.Count >= MaxPlayers)
                {
                    admitted = false;
                }
                else
                {
                    admitted = clients.TryAdd(clientId, user);
                }
                currentCount = clients.Count;
            }

            if (!admitted)
            {
                writeLog("Room is full (" + currentCount + "/" + MaxPlayers + "). Rejecting " + clientId);
                try { user.Send(getMsgCode(MsgCode.Full)); } catch { }
                user.Stop();
                try { client.Close(); } catch { }
                continue;
            }

            user.Send("Welcome, " + clientId);
            user.Send(getMsgCode(MsgCode.PlayerCount) + currentCount.ToString());
            Task UserReciever = new Task(() => { StartReceiving(user); });
            UserReciever.Start();
        }
    }

    // private bool OnUpdateGameSession()
    // {
    //     return true;
    //     //throw new NotImplementedException();
    // }

    // private void OnHealthCheck()
    // {
    //     writeLog("OnHealthCheck:" + DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss tt"));
    //     return;
    // }

    // private void OnProcessTerminate(UpdateGameSession updateGameSession)
    // {
    //     writeLog("OnProcessTerminate:" + DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss tt"));
    // }

    // private void OnStartGameSession(Aws.GameLift.Server.Model.GameSession gameSession)
    // {
    //     writeLog("OnStartGameSession: " + DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss tt"));
    //     writeLog(gameSession.DnsName);
    //     writeLog(gameSession.FleetId);
    //     writeLog("gameSession.GameProperties");
    //     foreach(KeyValuePair<string, string> property in gameSession.GameProperties)
    //     {
    //         writeLog(property.Key + ":" + property.Value);
    //     }
    //     writeLog(gameSession.GameSessionData);
    //     writeLog(gameSession.GameSessionId);
    //     writeLog(gameSession.IpAddress);
    //     writeLog(gameSession.Port.ToString());
    //     if(gameSession.MatchmakerData != null)
    //     {
    //         writeLog(gameSession.MatchmakerData);
    //     }
    //     else
    //     {
    //         writeLog("gameSession.MatchmakerData is null");
    //     }
    //     writeLog(gameSession.MaximumPlayerSessionCount.ToString());
    //     writeLog(gameSession.Name);
        

    //     GenericOutcome activateGameSessionOutcome = GameLiftServerAPI.ActivateGameSession();
    //     if(activateGameSessionOutcome.Success == false)
    //     {
    //         writeLog("Server failed to use the AWS GameLift SDK - ActivateGameSession");
    //         writeLog(activateGameSessionOutcome.Error.ToString());
    //         System.Threading.Thread.Sleep(2000);
    //         System.Environment.Exit(1);
    //     }
    // }

    public void StartReceiving(ChatClient user)
    {
        while (user.Alive)
        {
            try
            {
                string message = user.sr.ReadLine();

                if (!String.IsNullOrEmpty(message))
                {
                    if (message.StartsWith(getMsgCode(MsgCode.Leave)))
                    {
                        user.Stop();
                        ChatClient removed;
                        clients.TryRemove(user.ID, out removed);
                        writeLog(user.ID + " was leave.");
                    }
                    else if (message.StartsWith(getMsgCode(MsgCode.PlayerCount)))
                    {
                        user.Send(getMsgCode(MsgCode.PlayerCount) + clients.Count.ToString());
                    }
                    else if (message.StartsWith(getMsgCode(MsgCode.Name)))
                    {
                        writeLog("ID: " + user.ID);
                        writeLog("Original message: " + getMsgCodeName(message) + "-" + message);
                        writeLog("contents: " + getContents(message));
                        clients[user.ID].Name = getContents(message);
                    }
                    else if (message.StartsWith(getMsgCode(MsgCode.Ready)))
                    {
                        string playerSessionId = getContents(message);
                        // GenericOutcome acceptPlayerSessionOutcome = GameLiftServerAPI.AcceptPlayerSession(playerSessionId);
                        // if(acceptPlayerSessionOutcome.Success == false)
                        // {
                        //     writeLog("Server failed to use the AWS GameLift SDK - AcceptPlayerSession");
                        //     writeLog(acceptPlayerSessionOutcome.Error.ToString());
                        //     //System.Threading.Thread.Sleep(2000);
                        //     //System.Environment.Exit(1);
                        // }
                        // else
                        // {
                        //     writeLog(clients[user.ID].Name + " is validated.");
                        // }

                        clients[user.ID].IsReady = true;
                        if(clients.Count == 2 && clients.Values.All(c => c.IsReady))
                        {
                            // Both players are ready and the match is starting: self-allocate
                            // so Agones protects this GameServer from scale-down / fleet updates.
                            agones.Allocate();
                            BroadCast(user.ID, getMsgCode(MsgCode.Start));
                        }
                    }                    
                    else if (message.StartsWith(getMsgCode(MsgCode.Play)))
                    {
                        clients[user.ID].PlayerCard = int.Parse(getContents(message));
                        //Console.WriteLine("Changed name: " + message);

                        if(clients.Count == 2)
                        {
                            String[] keys = clients.Keys.ToArray();
                            int card0 = clients[keys[0]].PlayerCard;
                            int card1 = clients[keys[1]].PlayerCard;

                            // Both players have submitted a card for this round.
                            if(card0 != int.MinValue && card1 != int.MinValue)
                            {
                                if(card0 == card1)
                                {
                                    // Draw: notify both clients and start a new round.
                                    BroadCast(user.ID, getMsgCode(MsgCode.Draw));
                                    writeLog("Draw! Both played " + getCardName(card0) + ". Replay.");
                                    clients[keys[0]].PlayerCard = int.MinValue;
                                    clients[keys[1]].PlayerCard = int.MinValue;
                                }
                                else
                                {
                                    // Rock(1) beats Scissors(2), Scissors(2) beats Paper(3), Paper(3) beats Rock(1).
                                    // card0 wins when (card0 % 3) + 1 == card1.
                                    bool card0Wins = ((card0 % 3) + 1) == card1;
                                    string winnerKey = card0Wins ? keys[0] : keys[1];
                                    string loserKey = card0Wins ? keys[1] : keys[0];

                                    // Send the winner's Name so the client can match against its own ID.
                                    BroadCast(winnerKey, getMsgCode(MsgCode.GameOver) + clients[winnerKey].Name);
                                    int winnerCard = card0Wins ? card0 : card1;
                                    int loserCard = card0Wins ? card1 : card0;
                                    writeLog(clients[winnerKey].Name + " (" + winnerKey + "): win! (" + getCardName(winnerCard) + ")");
                                    writeLog(clients[loserKey].Name + " (" + loserKey + "): lose! (" + getCardName(loserCard) + ")");

                                    // GenericOutcome processEndingOutcome = GameLiftServerAPI.ProcessEnding();
                                    // if(processEndingOutcome.Success == false)
                                    // {
                                    //     writeLog("Server failed to use the AWS GameLift SDK - ProcessEnding");
                                    //     writeLog(processEndingOutcome.Error.ToString());
                                    //     System.Threading.Thread.Sleep(2000);
                                    //     System.Environment.Exit(1);
                                    // }

                                    // Match finished: ask Agones to shut the GameServer down so the Pod is recycled.
                                    agones.Shutdown();
                                    System.Threading.Thread.Sleep(2000);
                                    System.Environment.Exit(0);
                                }
                            }
                        }
                    }
                    else if (message.StartsWith(getMsgCode(MsgCode.Ping)))
                    {
                        writeLog(user.Name + ": ping", false);
                    }
                    else
                    {
                        writeLog(user.Name + ": " + message, false);
                        BroadCast(user.ID, message);
                    }
                }
                //Console.WriteLine(user.client.Connected.ToString() + user.Alive);
            }
            catch (Exception ex)
            {
                writeLog(ex.StackTrace);
                writeLog(ex.ToString());
                writeLog("COMMUNICATION ERROR: " + user.ID + " does net communicate. (" + ex.Message + ")");
                ChatClient removed;
                user.Stop();
                clients.TryRemove(user.ID, out removed);
                writeLog(removed.Name + " was removed.");
            }

        }   
    }

    public void BroadCast(string name, string message)
    {
        foreach (KeyValuePair<string, ChatClient> user in clients)
        {
            //if (name != user.Key)
            //{
                //user.Value.Send(name + ": " + message);
                user.Value.Send(message);
            //}
        }
    }
    
    private string getContents(string ori)
    {
        return ori.Substring(2);
    }

    private string getCardName(int card)
    {
        switch (card)
        {
            case 1: return "Rock";
            case 2: return "Scissors";
            case 3: return "Paper";
            default: return "Unknown";
        }
    }

    // Converts a message's 2-digit code prefix into its readable MsgCode name (e.g. "10" -> "Name").
    private string getMsgCodeName(string message)
    {
        string code = getMsgCode(message);
        if (int.TryParse(code, out int value) && Enum.IsDefined(typeof(MsgCode), value))
        {
            return ((MsgCode)value).ToString();
        }
        return code;
    }

    private string getMsgCode(MsgCode mc)
    {
        return ((int)mc).ToString();
    }

    public string getMsgCode(String mc)
    {
        return mc.Substring(0, 2);
    }

    void writeLog(String s) {
        Console.WriteLine(s);
        acw.CloudWatchLog(s == null ? "=== null ===" : s);
    }

    void writeLog(String s, bool cw) {
        Console.WriteLine(s);
        if(cw)
            acw.CloudWatchLog(s == null ? "=== null ===" : s);
    }
}
    
enum MsgCode
{
    Name = 10,
	PlayerCount = 11,
    Ready = 20,
    Start = 30,
    Play = 40,
    GameOver = 50,
	Win = 51,
	Lose = 52,
	Draw = 53,
    Join = 60,
    Leave = 70,
    Full = 71,
    Chat = 80,
    Ping = 90
}
