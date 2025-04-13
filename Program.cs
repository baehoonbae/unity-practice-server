using System.Net;
using System.Net.Sockets;
using System.Text;

// 플레이어의 현재 위치를 저장할 변수 (Main 메소드 바깥, 클래스 레벨에 선언해야 계속 유지됨)
// static 키워드를 붙여서 Main 메소드(static) 안에서 바로 접근 가능하게 함
int playerX = 0;
int playerY = 0;

TcpListener? server = null;
TcpClient? client = null;
NetworkStream? stream = null;

try
{
    int port = 7777;
    IPAddress localAddr = IPAddress.Parse("127.0.0.1");
    server = new TcpListener(localAddr, port);
    server.Start();
    Console.WriteLine("서버 시작! 클라이언트 접속 대기 중... (포트: 7777)");

    // 1. 클라이언트 연결 수락 (이 부분은 아직 한 번만 받음)
    client = await server.AcceptTcpClientAsync();
    Console.WriteLine($"클라이언트 접속! ({((IPEndPoint)client.Client.RemoteEndPoint!).Address})"); // 접속한 클라이언트 IP 표시

    // 데이터 통신 스트림 얻기
    stream = client.GetStream();
    Console.WriteLine("네트워크 스트림 열기 성공!");

    // 클라이언트가 접속하면 현재 위치(초기 위치)를 먼저 보내주기.
    try
    {
        string initialPositionMsg = $"POS,{playerX},{playerY}"; // "POS,x,y" 형식
        byte[] initialMsgBytes = Encoding.UTF8.GetBytes(initialPositionMsg);
        await stream.WriteAsync(initialMsgBytes, 0, initialMsgBytes.Length);
        Console.WriteLine($"초기 위치 전송: {initialPositionMsg}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"초기 위치 전송 중 오류: {e.Message}");
        // 여기서 연결을 끊어야 할 수도 있음
        throw; // 에러를 다시 던져서 아래 finally에서 처리하도록 함
    }

    // 2. 데이터 통신 스트림 얻기
    stream = client.GetStream();
    Console.WriteLine("네트워크 스트림 열기 성공!");

    byte[] bytes = new byte[1024]; // 버퍼 크기를 좀 더 넉넉하게

    // 3. 클라이언트가 연결되어 있는 동안 계속 메시지 받기 (무한 루프)
    while (true)
    {
        Console.WriteLine("클라이언트 메시지 대기 중...");
        int bytesRead = 0; // 매번 초기화

        try
        {
            // 데이터 읽기 시도 (클라이언트가 연결을 끊거나 데이터 보낼 때까지 여기서 기다림)
            bytesRead = await stream.ReadAsync(bytes, 0, bytes.Length);
        }
        catch (IOException ioEx)
        {
            // 읽기 중 네트워크 오류 발생 (클라이언트 강제 종료 등)
            Console.WriteLine($"클라이언트와 통신 중 오류 발생 (IOException): {ioEx.Message}");
            break; // 루프 탈출
        }
        catch (ObjectDisposedException disposedEx)
        {
            // 스트림이 이미 닫힌 경우
            Console.WriteLine($"스트림이 이미 닫혔습니다: {disposedEx.Message}");
            break; // 루프 탈출
        }


        if (bytesRead == 0)
        {
            // 클라이언트가 정상적으로 연결을 종료함
            Console.WriteLine("클라이언트가 연결을 끊었습니다.");
            break; // 루프 탈출
        }

        // 읽은 데이터 처리
        string receivedData = Encoding.UTF8.GetString(bytes, 0, bytesRead);
        Console.WriteLine($"수신: {receivedData}");

        // 받은 데이터에 따라 플레이어 위치 업데이트
        bool positionChanged = false; // 위치가 변경되었는지 확인하는 플래그
        if (receivedData == "UP")
        {
            playerY += 1; // Y 좌표 증가
            positionChanged = true;
        }
        else if (receivedData == "DOWN")
        {
            playerY -= 1; // Y 좌표 감소
            positionChanged = true;
        }
        else if (receivedData == "LEFT")
        {
            playerX -= 1; // X 좌표 감소
            positionChanged = true;
        }
        else if (receivedData == "RIGHT")
        {
            playerX += 1; // X 좌표 증가
            positionChanged = true;
        }

        if (positionChanged)
        {
            string positionUpdateMsg = $"POS,{playerX},{playerY}"; // "POS,x,y" 형식
            byte[] msg = Encoding.UTF8.GetBytes(positionUpdateMsg);
            try
            {
                await stream.WriteAsync(msg, 0, msg.Length);
                Console.WriteLine($"송신 (위치 업데이트): {positionUpdateMsg}");
            }
            catch (IOException ioEx)
            {
                Console.WriteLine($"위치 업데이트 전송 중 오류 발생 (IOException): {ioEx.Message}");
                break;
            }
            catch (ObjectDisposedException disposedEx)
            {
                Console.WriteLine($"위치 업데이트 전송 중 스트림이 닫힘: {disposedEx.Message}");
                break;
            }
        }
    }
}
catch (SocketException e)
{
    Console.WriteLine($"소켓 에러: {e}");
}
catch (Exception e)
{
    Console.WriteLine($"예상치 못한 에러: {e}");
}
finally
{
    stream?.Close();
    client?.Close();
    Console.WriteLine("클라이언트 소켓 및 스트림 정리 완료.");

    server?.Stop();
    Console.WriteLine("서버 리스너 종료.");
}

Console.WriteLine("서버 프로그램 종료. 아무 키나 누르세요...");
Console.ReadKey();