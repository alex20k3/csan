using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;


class PeerNetworkTalker
{
    private const int ANNOUNCE_UDP_PORT = 7777;
    private const int MIN_TCP_PORT = 9300;
    private const int MAX_TCP_PORT = 9400;

    static UdpClient udpAnnounceReceiver;
    static TcpListener tcpDirectLinkAccepter;
    static readonly List<TcpClient> tcpConnectedPartners = new List<TcpClient>();

    static string sessionUserName;
    static readonly HashSet<string> incomingMessageFingerprints = new HashSet<string>();
    static readonly List<string> conversationHistory = new List<string>();
    static int ownTcpListenPort;

    [DllImport("Kernel32")]
    private static extern bool SetConsoleCtrlHandler(OsSignalHandler callback, bool add);

    private delegate bool OsSignalHandler(OsSignalType sig);

    private enum OsSignalType
    {
        CTRL_C_EVENT = 0,
        CTRL_BREAK_EVENT = 1,
        CTRL_CLOSE_EVENT = 2,
        CTRL_LOGOFF_EVENT = 5,
        CTRL_SHUTDOWN_EVENT = 6
    }

    private static bool OnProgramTerminationSignal(OsSignalType signal)
    {
        if (signal == OsSignalType.CTRL_CLOSE_EVENT ||
            signal == OsSignalType.CTRL_C_EVENT ||
            signal == OsSignalType.CTRL_SHUTDOWN_EVENT)
        {
            Console.WriteLine("\n[СИСТЕМА] Получен сигнал завершения. Начинаю процедуру выхода...");
            PerformCleanExit();
            Environment.Exit(0);
        }
        return false;
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        Console.Write("Введите ваш псевдоним для чата: ");
        sessionUserName = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(sessionUserName))
        {
            sessionUserName = $"Пользователь{new Random().Next(100, 999)}";
            Console.WriteLine($"Имя не было введено. Вам присвоен псевдоним: {sessionUserName}");
        }

        Random rnd = new Random();
        ownTcpListenPort = rnd.Next(MIN_TCP_PORT, MAX_TCP_PORT);

        SetConsoleCtrlHandler(OnProgramTerminationSignal, true);

        SetupUdpAnnouncementListener();
        ActivateTcpDirectLinking();
        BroadcastSelfViaUdp();

        string systemMsg = $"[СИСТЕМА] Вы успешно вошли в чат как '{sessionUserName}'. Ваш TCP порт: {ownTcpListenPort}.";
        Console.WriteLine(systemMsg);
        conversationHistory.Add(systemMsg);

        Console.WriteLine("Для вывода истории введите 'архив'. Для выхода - 'покинуть'.");
        while (true)
        {
            string userInput = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(userInput)) continue;

            if (userInput.Trim().ToLowerInvariant() == "архив")
            {
                ShowConversationLog();
                continue;
            }
            if (userInput.Trim().ToLowerInvariant() == "покинуть")
            {
                PerformCleanExit();
                break;
            }

            string messageToSend = $"{DateTime.Now:dd.MM.yy HH:mm:ss} ({sessionUserName}): {userInput}";

            Console.SetCursorPosition(0, Console.CursorTop > 0 ? Console.CursorTop - 1 : 0);
            int currentLineCursor = Console.CursorTop;
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, currentLineCursor);
            Console.WriteLine(messageToSend);

            conversationHistory.Add($"[Я ОТПРАВИЛ] {messageToSend}");
            DistributeToAllTcpPartners(messageToSend);
        }
        Console.WriteLine("[СИСТЕМА] Вы вышли из чата.");
        Environment.Exit(0);
    }

    static void ShowConversationLog()
    {
        Console.WriteLine("\n--- Журнал текущей сессии ---");
        foreach (var entry in conversationHistory)
        {
            if (entry.StartsWith("[Я ОТПРАВИЛ] "))
                Console.WriteLine(entry.Substring("[Я ОТПРАВИЛ] ".Length));
            else if (entry.StartsWith("[МНЕ ПРИСЛАЛИ] "))
                Console.WriteLine(entry.Substring("[МНЕ ПРИСЛАЛИ] ".Length));
            else
                Console.WriteLine(entry);
        }
        Console.WriteLine("--- Конец журнала ---\n");
    }

    static void PerformCleanExit()
    {
        Console.WriteLine("[СИСТЕМА] Закрываю соединения и уведомляю других участников...");
        string farewellMessage = $"[УЧАСТНИК_ВЫШЕЛ] {sessionUserName}";

        DistributeToAllTcpPartners(farewellMessage);

        Thread.Sleep(300);

        lock (tcpConnectedPartners)
        {
            foreach (var partner in tcpConnectedPartners)
            {
                try { partner.Close(); } catch { }
            }
            tcpConnectedPartners.Clear();
        }

        try { udpAnnounceReceiver?.Close(); }
        catch (Exception ex) { Console.WriteLine($"[СИСТЕМА] Ошибка при закрытии UDP приемника: {ex.Message}"); }

        try { tcpDirectLinkAccepter?.Stop(); }
        catch (Exception ex) { Console.WriteLine($"[СИСТЕМА] Ошибка при остановке TCP слушателя: {ex.Message}"); }

        Console.WriteLine("[СИСТЕМА] Все сетевые операции завершены.");
    }

    static void SetupUdpAnnouncementListener()
    {
        try
        {
            udpAnnounceReceiver = new UdpClient();
            udpAnnounceReceiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpAnnounceReceiver.Client.Bind(new IPEndPoint(IPAddress.Any, ANNOUNCE_UDP_PORT));

            Thread listenerThread = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        IPEndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                        byte[] receivedData = udpAnnounceReceiver.Receive(ref remoteEndpoint);
                        string announcement = Encoding.UTF8.GetString(receivedData);

                        string[] parts = announcement.Split(':');
                        if (parts.Length == 2)
                        {
                            string discoveredUserName = parts[0];
                            if (int.TryParse(parts[1], out int discoveredTcpPort))
                            {
                                if (discoveredUserName == sessionUserName && discoveredTcpPort == ownTcpListenPort)
                                {
                                    continue;
                                }
                                Console.WriteLine($"[СИСТЕМА] Обнаружен участник '{discoveredUserName}' ({remoteEndpoint.Address}:{discoveredTcpPort}). Пытаюсь подключиться...");
                                CreateTcpLinkToPeer(remoteEndpoint.Address.ToString(), discoveredTcpPort, discoveredUserName);
                            }
                        }
                    }
                }
                catch (SocketException se) when (se.SocketErrorCode == SocketError.Interrupted || se.SocketErrorCode == SocketError.OperationAborted)
                {
                    Console.WriteLine("[СИСТЕМА] UDP приемник анонсов остановлен.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[СИСТЕМА] Критическая ошибка в UDP приемнике: {ex.Message}");
                }
            });
            listenerThread.IsBackground = true;
            listenerThread.Name = "UdpDiscoveryThread";
            listenerThread.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[СИСТЕМА] Не удалось запустить UDP приемник на порту {ANNOUNCE_UDP_PORT}: {ex.Message}. Обнаружение других участников будет невозможно.");
        }
    }

    static void BroadcastSelfViaUdp()
    {
        try
        {
            using (var broadcaster = new UdpClient())
            {
                broadcaster.EnableBroadcast = true;
                string selfAnnouncement = $"{sessionUserName}:{ownTcpListenPort}";
                byte[] dataBytes = Encoding.UTF8.GetBytes(selfAnnouncement);
                broadcaster.Send(dataBytes, dataBytes.Length, new IPEndPoint(IPAddress.Broadcast, ANNOUNCE_UDP_PORT));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[СИСТЕМА] Не удалось отправить широковещательный анонс: {ex.Message}");
        }
    }

    static void ActivateTcpDirectLinking()
    {
        try
        {
            tcpDirectLinkAccepter = new TcpListener(IPAddress.Any, ownTcpListenPort);
            tcpDirectLinkAccepter.Start();

            Thread accepterThread = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        TcpClient newPartnerConnection = tcpDirectLinkAccepter.AcceptTcpClient();

                        IPEndPoint partnerEndpoint = newPartnerConnection.Client.RemoteEndPoint as IPEndPoint;
                        string partnerInfo = partnerEndpoint != null ? $"{partnerEndpoint.Address}:{partnerEndpoint.Port}" : "неизвестный адрес";
                        Console.WriteLine($"[СИСТЕМА] Принято входящее TCP соединение от {partnerInfo}.");

                        lock (tcpConnectedPartners)
                        {
                            tcpConnectedPartners.Add(newPartnerConnection);
                        }

                        Thread partnerHandlerThread = new Thread(() => HandleTcpPeerStream(newPartnerConnection));
                        partnerHandlerThread.IsBackground = true;
                        partnerHandlerThread.Name = $"TcpHandlerFor_{partnerInfo}";
                        partnerHandlerThread.Start();
                    }
                }
                catch (SocketException se) when (se.SocketErrorCode == SocketError.Interrupted || se.SocketErrorCode == SocketError.OperationAborted)
                {
                    Console.WriteLine("[СИСТЕМА] TCP слушатель соединений остановлен.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[СИСТЕМА] Критическая ошибка в TCP слушателе: {ex.Message}");
                }
            });
            accepterThread.IsBackground = true;
            accepterThread.Name = "TcpAccepterThread";
            accepterThread.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[СИСТЕМА] Не удалось запустить TCP слушатель на порту {ownTcpListenPort}: {ex.Message}. Прием входящих подключений невозможен.");
        }
    }

    static void CreateTcpLinkToPeer(string partnerIpAddress, int partnerTcpPort, string partnerUserName)
    {
        lock (tcpConnectedPartners)
        {
            if (tcpConnectedPartners.Any(p =>
                p.Connected &&
                p.Client.RemoteEndPoint is IPEndPoint ep &&
                ep.Address.ToString() == partnerIpAddress &&
                ep.Port == partnerTcpPort))
            {
                return;
            }
        }

        try
        {
            TcpClient partnerClient = new TcpClient();
            IAsyncResult connectionResult = partnerClient.BeginConnect(partnerIpAddress, partnerTcpPort, null, null);
            bool connectedSuccessfully = connectionResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3));

            if (connectedSuccessfully && partnerClient.Connected)
            {
                partnerClient.EndConnect(connectionResult);

                lock (tcpConnectedPartners)
                {
                    tcpConnectedPartners.Add(partnerClient);
                }
                Console.WriteLine($"[СИСТЕМА] Успешно установлено TCP соединение с {partnerUserName} ({partnerIpAddress}:{partnerTcpPort}).");
                conversationHistory.Add($"[СИСТЕМА] Подключился к {partnerUserName}.");

                DistributeToAllTcpPartners($"[НОВЫЙ_УЧАСТНИК] {sessionUserName}");

                Thread peerStreamHandlerThread = new Thread(() => HandleTcpPeerStream(partnerClient));
                peerStreamHandlerThread.IsBackground = true;
                peerStreamHandlerThread.Name = $"TcpStreamHandler_{partnerIpAddress}_{partnerTcpPort}";
                peerStreamHandlerThread.Start();
            }
            else
            {
                if (partnerClient.Connected) partnerClient.Close();
                Console.WriteLine($"[СИСТЕМА] Не удалось подключиться к {partnerUserName} ({partnerIpAddress}:{partnerTcpPort}) в течение 3 секунд.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[СИСТЕМА] Ошибка при попытке TCP подключения к {partnerUserName} ({partnerIpAddress}:{partnerTcpPort}): {ex.Message}");
        }
    }

    static void HandleTcpPeerStream(TcpClient partnerClient)
    {
        NetworkStream netStream = null;
        StreamReader sReader = null;
        IPEndPoint remoteEpInfo = partnerClient.Client.RemoteEndPoint as IPEndPoint;
        string partnerIdentifier = remoteEpInfo != null ? $"{remoteEpInfo.Address}:{remoteEpInfo.Port}" : "неизвестный партнер";

        try
        {
            netStream = partnerClient.GetStream();
            sReader = new StreamReader(netStream, Encoding.UTF8);

            while (partnerClient.Connected)
            {
                string receivedText = sReader.ReadLine();

                if (receivedText == null)
                {
                    Console.WriteLine($"[СИСТЕМА] Партнер {partnerIdentifier} разорвал соединение.");
                    break;
                }

                if (receivedText.Contains($"({sessionUserName}): ") ||
                    receivedText == $"[НОВЫЙ_УЧАСТНИК] {sessionUserName}" ||
                    receivedText == $"[УЧАСТНИК_ВЫШЕЛ] {sessionUserName}")
                {
                    continue;
                }

                bool isDuplicate;
                lock (incomingMessageFingerprints)
                {
                    isDuplicate = incomingMessageFingerprints.Contains(receivedText);
                    if (!isDuplicate)
                    {
                        incomingMessageFingerprints.Add(receivedText);
                        if (incomingMessageFingerprints.Count > 1500)
                        {
                            var first = incomingMessageFingerprints.FirstOrDefault();
                            if (first != null) incomingMessageFingerprints.Remove(first);
                        }
                    }
                }

                if (isDuplicate)
                {
                    continue;
                }

                if (receivedText.StartsWith("[НОВЫЙ_УЧАСТНИК] "))
                {
                    string joinedUserName = receivedText.Substring("[НОВЫЙ_УЧАСТНИК] ".Length);
                    string notification = $"[ЧАТ] Участник '{joinedUserName}' присоединился.";
                    Console.WriteLine(notification);
                    conversationHistory.Add(notification);
                    DistributeToAllTcpPartners(receivedText);
                }
                else if (receivedText.StartsWith("[УЧАСТНИК_ВЫШЕЛ] "))
                {
                    string exitedUserName = receivedText.Substring("[УЧАСТНИК_ВЫШЕЛ] ".Length);
                    string notification = $"[ЧАТ] Участник '{exitedUserName}' покинул чат.";
                    Console.WriteLine(notification);
                    conversationHistory.Add(notification);
                    DistributeToAllTcpPartners(receivedText);
                }
                else
                {
                    Console.WriteLine(receivedText);
                    conversationHistory.Add($"[МНЕ ПРИСЛАЛИ] {receivedText}");
                    DistributeToAllTcpPartners(receivedText);
                }
            }
        }
        catch (IOException ioEx) when (ioEx.InnerException is SocketException socketEx &&
                                      (socketEx.SocketErrorCode == SocketError.ConnectionReset ||
                                       socketEx.SocketErrorCode == SocketError.ConnectionAborted))
        {
            Console.WriteLine($"[СИСТЕМА] Соединение с партнером {partnerIdentifier} было принудительно разорвано.");
        }
        catch (Exception ex)
        {
            if (partnerClient.Connected)
            {
                Console.WriteLine($"[СИСТЕМА] Ошибка при обработке данных от партнера {partnerIdentifier}: {ex.Message}");
            }
        }
        finally
        {
            sReader?.Close();
            netStream?.Close();

            lock (tcpConnectedPartners)
            {
                tcpConnectedPartners.Remove(partnerClient);
            }
            try { partnerClient.Close(); } catch { }
            Console.WriteLine($"[СИСТЕМА] Сессия с партнером {partnerIdentifier} завершена.");
        }
    }

    static void DistributeToAllTcpPartners(string textPayload)
    {
        if (string.IsNullOrEmpty(textPayload)) return;

        byte[] dataBuffer = Encoding.UTF8.GetBytes(textPayload + "\n");

        List<TcpClient> partnersToRemove = new List<TcpClient>();
        List<TcpClient> currentPartnersView;

        lock (tcpConnectedPartners)
        {
            currentPartnersView = new List<TcpClient>(tcpConnectedPartners);
        }

        foreach (var partner in currentPartnersView)
        {
            try
            {
                if (partner.Connected)
                {
                    NetworkStream stream = partner.GetStream();
                    stream.Write(dataBuffer, 0, dataBuffer.Length);
                    stream.Flush();
                }
                else
                {
                    partnersToRemove.Add(partner);
                }
            }
            catch (Exception ex)
            {
                IPEndPoint ep = partner.Client.RemoteEndPoint as IPEndPoint;
                string pInfo = ep != null ? ep.Address.ToString() : "неизвестный";
                Console.WriteLine($"[СИСТЕМА] Не удалось отправить сообщение партнеру {pInfo}: {ex.GetType().Name}. Партнер будет удален.");
                partnersToRemove.Add(partner);
            }
        }

        if (partnersToRemove.Any())
        {
            lock (tcpConnectedPartners)
            {
                foreach (var deadPartner in partnersToRemove)
                {
                    tcpConnectedPartners.Remove(deadPartner);
                    try { deadPartner.Close(); } catch { }
                }
            }
        }
    }
}