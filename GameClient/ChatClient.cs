//client
using System.Net.Sockets;

class ChatClient
{
	public TcpClient client;
	public StreamWriter sw { get; private set; }
	public StreamReader sr { get; private set; }
	public string ID;
	public string Name { get; set; }
	public  bool Alive;
	int playerCount = 0;
	public bool IsReady { get; set; }
	public int PlayerCard { get; set; }
	public PlayState PlayStatus { get; set; }
	public int PlayerCount { get; set; }

	public ChatClient(TcpClient client, string ID)
	{
		this.client = client;
		sw = new StreamWriter(client.GetStream());
		sr = new StreamReader(client.GetStream());
		Thread receiveThread = new Thread(new ThreadStart(run));
		receiveThread.IsBackground = true;
		receiveThread.Start();
		Alive = true;
		this.ID = ID;
		this.IsReady = false;
		this.PlayStatus = PlayState.Waiting;
		PlayerCard = int.MinValue;
		PlayerCount = 1;
		this.Send(getMsgCode(MsgCode.Name) + ID);
		Console.WriteLine("This is " + ID);
	}

	public void Send(string message)
	{
		try
		{
			sw.WriteLine(message);
			sw.Flush();
		}
		catch (Exception)
		{
			// The connection is gone (e.g. server rejected us because the room was full,
			// or the peer dropped). Mark the client dead and signal the main loop to exit
			// cleanly instead of crashing the process on a closed socket.
			Alive = false;
			this.PlayStatus = PlayState.Finished;
		}
	}

	public void run()
	{
		string message = String.Empty;
		try
		{
			if (client.Connected && sr != null)
			{
				while ((message = sr.ReadLine()) != null)
				{
					if (message.StartsWith(getMsgCode(MsgCode.PlayerCount)))
					{
						this.PlayerCount = int.Parse(getContents(message));
						Console.WriteLine(MsgCode.PlayerCount + ": " + PlayerCount);
					}
					else if (message.StartsWith(getMsgCode(MsgCode.Start)))
					{
						this.IsReady = true;
						this.PlayStatus = PlayState.Playing;
						Console.WriteLine(MsgCode.Start);
					}
					else if (message.StartsWith(getMsgCode(MsgCode.Draw)))
					{
						this.IsReady = true;
						this.PlayStatus = PlayState.Playing;
						Console.WriteLine(MsgCode.Draw);
					}					
					else if (message.StartsWith(getMsgCode(MsgCode.Full)))
					{
						Console.WriteLine("Server is full. Cannot join the game.");
						this.PlayStatus = PlayState.Finished;
					}
					else if (message.StartsWith(getMsgCode(MsgCode.GameOver)))
					{
						Console.WriteLine("GameOver: " + getContents(message));
						this.PlayStatus = PlayState.Finished;
						if (this.ID == getContents(message))
						{
							Console.WriteLine("You win");
						}
						else
						{
							Console.WriteLine("You lose");
						}

						Console.WriteLine(MsgCode.GameOver);
						//client.Close();
					}
					else
					{
						Console.WriteLine("Received: " + message);
						Console.WriteLine("Received: " + message.StartsWith(getMsgCode(MsgCode.Start)));
					}
				}
			}
		}
		catch (Exception) { Console.WriteLine("error"); }
	}

	public void Stop()
	{
		Alive = false;
	}
    private string getContents(string ori)
    {
        return ori.Substring(2);
    }

    public string getMsgCode(MsgCode mc)
    {
        return ((int)mc).ToString();
    }

	public string getMsgCode(String mc)
    {
        return mc.Substring(0, 2);
    }	
}

enum GameResult
{
    Win = 2,
    Lose = 1,
    Draw = 0
}

enum PlayState
{
	Waiting = 0,
	Playing = 1,
	Finished = 2
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
