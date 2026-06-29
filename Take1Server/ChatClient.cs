//client
using System.Net.Sockets;

class ChatClient
{
	public TcpClient client;
	public StreamWriter sw { get; private set; }
	public StreamReader sr { get; private set; }
	public string ID { get; set; }
	public string Name { get; set; }
	public  bool Alive;
	int playerCount = 0;
	public bool IsReady { get; set; }
	public int PlayerCard { get; set; }	

	public ChatClient(TcpClient client, string ID)
	{
		this.client = client;
		sw = new StreamWriter(client.GetStream());
		sr = new StreamReader(client.GetStream());
		Alive = true;
		this.ID = ID;
		PlayerCard = int.MinValue;
	}

	public void Send(string message)
	{
		sw.WriteLine(message);
		sw.Flush();
	}

	public void Stop()
	{
		Alive = false;
	}
}