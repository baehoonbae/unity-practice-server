using System.Net.Sockets;

namespace SimpleTcpServer;

public class PlayerData(TcpClient client)
{
    public TcpClient Client { get; } = client;
    public NetworkStream Stream { get; } = client.GetStream(); 
    public string PlayerId { get; } = client.Client.RemoteEndPoint!.ToString() ?? "unknown";
    public int X { get; set; } = 0;
    public int Y { get; set; } = 0;


    // 나중에 플레이어 연결 끊을 때 정리하는 메소드도 추가하면 좋음
    public void Close()
    {
        Console.WriteLine($"PlayerData Close() 호출됨: {PlayerId}");
        try { Stream.Close(); } catch { /* Ignore */ }
        try { Client.Close(); } catch { /* Ignore */ }
    }
}