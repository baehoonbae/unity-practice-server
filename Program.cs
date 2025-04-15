using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SimpleTcpServer;

internal abstract class Program
{
    // 모든 접속된 플레이어 정보를 담는 리스트 (☆중요☆ 여러 스레드에서 접근하므로 static으로 선언하고 lock 필요)
    private static readonly List<PlayerData> Players = new List<PlayerData>();
    private static readonly object PlayerListLock = new object(); // lock을 위한 객체

    static async Task Main(string[] args)
    {
        TcpListener? server = null;
        try
        {
            int port = 7777;
            // IPAddress localAddr = IPAddress.Parse("127.0.0.1"); // 로컬에서만 접속 허용
            IPAddress localAddr = IPAddress.Any; // 모든 IP에서 접속 허용 (외부 테스트 시 필요)
            server = new TcpListener(localAddr, port);
            server.Start();
            Console.WriteLine($"서버 시작! ({localAddr}:{port}) 여러 클라이언트 접속 대기 중...");

            // 쉴 새 없이 새 클라이언트 접속을 기다림
            while (true)
            {
                Console.WriteLine("새 클라이언트 연결 대기...");
                TcpClient acceptedClient = await server.AcceptTcpClientAsync();

                // 1. 새 플레이어 정보 카드 만들기
                PlayerData newPlayer = new PlayerData(acceptedClient);
                Console.WriteLine($"클라이언트 접속! ID: {newPlayer.PlayerId}");

                // 2. 전체 플레이어 명단에 추가 (☆중요☆ lock 사용)
                lock (PlayerListLock)
                {
                    Players.Add(newPlayer);
                }

                // 3. 이 플레이어와의 통신을 별도 스레드(Task)에서 처리 시작
                Task.Run(() => HandleClientAsync(newPlayer));

                // (선택 사항) 서버 로그에 현재 접속자 수 표시
                lock (PlayerListLock)
                {
                    Console.WriteLine($"현재 접속자 수: {Players.Count}");
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
            // 서버 종료 시 모든 클라이언트 연결 정리 (실제로는 Ctrl+C 등으로 종료 시 이 부분 실행 어려움)
            Console.WriteLine("서버 종료 절차 시작...");
            lock (PlayerListLock)
            {
                foreach (var player in Players)
                {
                    try
                    {
                        player.Close();
                    }
                    catch
                    {
                        /* Ignore */
                    }
                }

                Players.Clear();
                Console.WriteLine("모든 클라이언트 연결 정리 시도 완료.");
            }

            server?.Stop();
            Console.WriteLine("서버 리스너 종료.");
        }

        Console.WriteLine("서버 프로그램 종료. 아무 키나 누르세요...");
        Console.ReadKey(); // 실제 서버에서는 이 부분 필요 없을 수 있음
    }

    // 각 클라이언트와의 통신을 전담하는 메소드 (Task로 실행됨)
    static async Task HandleClientAsync(PlayerData player)
    {
        NetworkStream stream = player.Stream; // PlayerData에서 stream 가져오기
        byte[] buffer = new byte[1024];

        try
        {
            // --- 처음 접속 시 처리 ---
            List<string> initialMessages = new List<string>();

            // 1. 새 플레이어에게 현재 다른 모든 플레이어들의 위치 정보 보내주기 + 자기 정보도 준비
            lock (PlayerListLock) // 명단 읽을 때도 lock!
            {
                // 자기 자신의 초기 위치 정보
                initialMessages.Add($"POS,{player.PlayerId},{player.X},{player.Y}"); // "POS,id,x,y" 형식

                // 다른 플레이어들의 정보
                foreach (var p in Players)
                {
                    if (p.PlayerId != player.PlayerId) // 자기 자신 빼고
                    {
                        initialMessages.Add($"JOIN,{p.PlayerId},{p.X},{p.Y}"); // "JOIN,id,x,y" 형식
                    }
                }
            }

            // 준비된 초기 메시지들을 한 번에 보내기 (개행 문자로 구분)
            string initialData = string.Join("\n", initialMessages);
            await SendMessageAsync(player, initialData);
            Console.WriteLine($"[{player.PlayerId}]에게 초기 정보 전송:\n{initialData}");


            // 2. 이 새로운 플레이어가 접속했다는 사실을 다른 모든 플레이어에게 알리기 (위치 포함)
            string joinMsg = $"JOIN,{player.PlayerId},{player.X},{player.Y}"; // "JOIN,id,x,y" 형식
            await BroadcastMessageAsync(joinMsg, player); // 나 빼고 모두에게 전송
            Console.WriteLine($"모두에게 전파: {player.PlayerId} 입장 ({player.X},{player.Y})");


            // --- 계속 메시지 받기 ---
            while (true)
            {
                // 클라이언트 연결 상태 확인 (선택 사항, 더 확실한 확인)
                if (!player.Client.Connected || !IsSocketConnected(player.Client.Client))
                {
                    Console.WriteLine($"클라이언트 연결 감지 실패 (소켓 상태): {player.PlayerId}");
                    break;
                }

                Console.WriteLine($"[{player.PlayerId}] 메시지 대기 중...");
                int bytesRead = 0;
                try
                {
                    // 읽기 작업에 타임아웃 설정 (예: 60초). 0이면 무한 대기.
                    // stream.ReadTimeout = 60000; // 필요에 따라 설정
                    bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                }
                catch (IOException ex) when (ex.InnerException is SocketException
                                             {
                                                 SocketErrorCode: SocketError.TimedOut
                                             })
                {
                    Console.WriteLine($"[{player.PlayerId}] 메시지 수신 타임아웃.");
                    // 타임아웃 시 연결을 끊을 수도 있고, 계속 대기할 수도 있음. 여기서는 계속 대기.
                    continue;
                }
                catch (IOException ioEx)
                {
                    // 읽기 중 네트워크 오류 발생 (클라이언트 강제 종료 등)
                    Console.WriteLine($"[{player.PlayerId}]와 통신 중 오류 발생 (IOException): {ioEx.Message}");
                    break; // 루프 탈출
                }
                catch (ObjectDisposedException)
                {
                    Console.WriteLine($"[{player.PlayerId}] 스트림이 이미 닫혔습니다 (ReadAsync).");
                    break; // 루프 탈출
                }

                if (bytesRead == 0)
                {
                    Console.WriteLine($"클라이언트 연결 끊김 (정상 종료 감지): {player.PlayerId}");
                    break; // 루프 종료 -> 아래 finally 로 이동
                }

                string receivedData = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"[{player.PlayerId}] 수신: {receivedData}");

                // 받은 데이터 처리 (여기서는 간단히 문자열 비교)
                bool positionChanged = false;
                if (receivedData.Trim() == "UP")
                {
                    player.Y += 1;
                    positionChanged = true;
                }
                else if (receivedData.Trim() == "DOWN")
                {
                    player.Y -= 1;
                    positionChanged = true;
                }
                else if (receivedData.Trim() == "LEFT")
                {
                    player.X -= 1;
                    positionChanged = true;
                }
                else if (receivedData.Trim() == "RIGHT")
                {
                    player.X += 1;
                    positionChanged = true;
                }
                // 실제 게임에서는 메시지 형식을 더 구조화하는 것이 좋음 (예: "MOVE,UP")

                if (positionChanged)
                {
                    // 새 위치 정보를 "모든" 플레이어에게 전송!
                    string positionUpdateMsg = $"POS,{player.PlayerId},{player.X},{player.Y}"; // "POS,id,x,y" 형식
                    Console.WriteLine($"[{player.PlayerId}] 위치 변경 -> 모두에게 전파: {positionUpdateMsg}");
                    await BroadcastMessageAsync(positionUpdateMsg, null); // 모든 사람에게 (null은 '모든 사람' 의미)
                }
                else
                {
                    Console.WriteLine($"[{player.PlayerId}] 알 수 없는 메시지 수신: {receivedData}");
                    // 필요하다면 에러 메시지 응답
                    // await SendMessageAsync(player, "ERR,Unknown command");
                }
            }
        }
        catch (Exception e) // 예상치 못한 오류 처리
        {
            Console.WriteLine($"클라이언트 처리 중 예외 발생 ID: {player.PlayerId}, 오류: {e}");
        }
        finally
        {
            // --- 연결 종료 처리 ---
            Console.WriteLine($"클라이언트 연결 종료 처리 시작: {player.PlayerId}");

            // 1. 플레이어 목록에서 제거 (☆중요☆ lock 사용)
            bool removed = false;
            lock (PlayerListLock)
            {
                removed = Players.Remove(player);
            }

            // 2. 리소스 정리
            try
            {
                player.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{player.PlayerId}] 리소스 정리 중 오류: {ex.Message}");
            }

            if (removed)
            {
                Console.WriteLine($"플레이어 목록에서 제거 완료: {player.PlayerId}. 현재 접속자 수: {Players.Count}");
                // 3. 다른 모든 플레이어에게 이 플레이어가 나갔다고 알리기
                string leaveMsg = $"LEAVE,{player.PlayerId}"; // "LEAVE,id" 형식
                await BroadcastMessageAsync(leaveMsg, null); // 이미 목록에서 제거했으므로 그냥 null 사용
                Console.WriteLine($"모두에게 전파: {player.PlayerId} 퇴장");
            }
            else
            {
                Console.WriteLine($"플레이어 목록에서 제거 실패 (이미 없음?): {player.PlayerId}");
            }
        }
    }

// 모든 플레이어(또는 sender 제외)에게 메시지 전송 (비동기)
    static async Task BroadcastMessageAsync(string message, PlayerData? sender)
    {
        List<PlayerData> playersToSend;
        lock (PlayerListLock) // 현재 접속한 플레이어 목록을 안전하게 복사
        {
            playersToSend = Players.ToList(); // 새 리스트로 복사해서 사용 (반복 중 컬렉션 변경 방지)
        }

        byte[] messageBytes = Encoding.UTF8.GetBytes(message + "\n"); // 메시지 끝에 개행 문자 추가 (클라이언트에서 구분 용이)

        // 여러 클라이언트에게 동시에 보내기 위해 Task 리스트 사용
        List<Task> broadcastTasks = new List<Task>();

        Console.WriteLine(
            $"브로드캐스트 시작: \"{message}\" (대상: {playersToSend.Count}명{(sender != null ? $", 발신자 제외: {sender.PlayerId}" : "")})");
        foreach (var player in playersToSend)
        {
            if (player != sender) // 메시지 보낸 사람(sender)은 제외 (sender가 null이면 모두에게 보냄)
            {
                // 각 전송 작업을 Task로 추가
                broadcastTasks.Add(SendMessageAsync(player, message, messageBytes));
            }
        }

        // 모든 비동기 전송 작업이 완료될 때까지 기다림 (선택 사항)
        try
        {
            await Task.WhenAll(broadcastTasks);
            //Console.WriteLine($"브로드캐스트 완료: \"{message}\" ({broadcastTasks.Count}명에게 성공적으로 시도)");
        }
        catch (Exception ex)
        {
            // Task.WhenAll에서 예외가 발생할 수 있음 (개별 SendMessageAsync 오류는 거기서 처리됨)
            Console.WriteLine($"브로드캐스트 중 하나 이상의 작업에서 오류 발생: {ex.Message}");
        }
    }

// 특정 플레이어에게 메시지 전송 (비동기)
    static async Task SendMessageAsync(PlayerData player, string message, byte[]? messageBytes = null)
    {
        // 스트림이 유효하고 클라이언트가 연결된 상태인지 확인
        if (player?.Stream == null || !player.Client.Connected || !IsSocketConnected(player.Client.Client))
        {
            Console.WriteLine($"[{player?.PlayerId ?? "N/A"}] 메시지 전송 불가 (연결 끊김 또는 스트림 없음)");
            return; // 연결이 끊겼거나 스트림이 없으면 보내지 않음
        }

        try
        {
            byte[] bytesToSend = messageBytes ?? Encoding.UTF8.GetBytes(message + "\n"); // 메시지 끝에 개행 문자 추가
            await player.Stream.WriteAsync(bytesToSend, 0, bytesToSend.Length);
            // 너무 자주 찍히면 아래 로그는 주석 처리
            // Console.WriteLine($"[{player.PlayerId}]에게 송신: {message}");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[{player.PlayerId}]에게 메시지 전송 실패 (IOException): {ex.Message}");
            // 여기서 연결 종료 처리를 즉시 트리거할 수도 있음 (더 복잡하지만 안정적)
            // 예: Task.Run(() => HandleClientDisconnection(player));
        }
        catch (ObjectDisposedException)
        {
            Console.WriteLine($"[{player.PlayerId}]에게 메시지 전송 실패 (Stream/Socket closed)");
            // 여기서도 연결 종료 처리 트리거 가능
        }
        catch (Exception ex) // 기타 예외
        {
            Console.WriteLine($"[{player.PlayerId}]에게 메시지 전송 중 예외 발생: {ex.GetType().Name} - {ex.Message}");
        }
    }

    // 소켓 연결 상태를 좀 더 확실하게 확인하는 함수 (선택 사항)
    static bool IsSocketConnected(Socket? s)
    {
        if (s == null || !s.Connected) return false;

        // Poll을 사용하여 소켓 상태 확인 (Non-blocking)
        // SelectRead 모드: 읽을 데이터가 있거나 연결이 닫혔는지 확인
        // Available == 0: 읽을 데이터가 없다는 의미. 이 상태에서 Poll이 true를 반환하면 연결이 닫힌 것.
        try
        {
            return !(s.Poll(1000, SelectMode.SelectRead) && s.Available == 0);
        }
        catch (SocketException)
        {
            return false;
        } // Poll 자체가 에러나면 연결 끊긴 것으로 간주
        catch (ObjectDisposedException)
        {
            return false;
        } // 이미 Dispose된 경우
    }
}