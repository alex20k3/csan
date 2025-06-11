using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Proxy
{
    internal class Program
    {
        private const int BUFFER_SIZE = 8192;
        private const int LISTEN_PORT = 8888;  // Прокси-сервер слушает этот порт

        public static void Main()
        {
            TcpListener listener = new TcpListener(IPAddress.Any, LISTEN_PORT);
            listener.Start();
            Console.WriteLine($"[*] Proxy-сервер запущен на порту {LISTEN_PORT}");

            while (true)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    Thread thread = new Thread(() => HandleClient(client));
                    thread.Start();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Ошибка] Проблема с подключением клиента: " + ex.Message);
                }
            }
        }

        private static void HandleClient(TcpClient client)
        {
            using (NetworkStream clientStream = client.GetStream())
            {
                try
                {
                    byte[] buffer = new byte[BUFFER_SIZE];
                    int bytesRead = clientStream.Read(buffer, 0, buffer.Length);

                    if (bytesRead == 0) return;

                    string requestStr = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                    string[] requestLines = requestStr.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string requestLine = requestLines[0];

                    string[] tokens = requestLine.Split(' ');
                    if (tokens.Length < 3) return;

                    string method = tokens[0];
                    string fullUrl = tokens[1];

                    Uri uri = new Uri(fullUrl);
                    string host = uri.Host;
                    int port = uri.IsDefaultPort ? 80 : uri.Port;
                    string path = uri.PathAndQuery;

                    
                    Console.WriteLine($"[LOG] {method} {fullUrl}");

                    
                    bool isConnected = TestConnection(host, port);
                    if (!isConnected)
                    {
                        Console.WriteLine($"[ERROR] Не удалось подключиться к серверу {host}:{port}");
                        return; 
                    }

                    requestLines[0] = $"{method} {path} HTTP/1.1";
                    string modifiedRequest = string.Join("\r\n", requestLines) + "\r\n\r\n";

                    TcpClient server = new TcpClient(host, port);
                    using (NetworkStream serverStream = server.GetStream())
                    {
                        byte[] requestBytes = Encoding.ASCII.GetBytes(modifiedRequest);
                        serverStream.Write(requestBytes, 0, requestBytes.Length);

                        byte[] responseBuffer = new byte[BUFFER_SIZE];
                        int responseBytes = serverStream.Read(responseBuffer, 0, responseBuffer.Length);
                        string responseHeader = Encoding.ASCII.GetString(responseBuffer, 0, responseBytes);

                        string statusLine = responseHeader.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                        string statusCode = statusLine.Split(' ')[1];

                        Console.WriteLine($"[LOG] Ответ от {host}: {statusCode}");

                        clientStream.Write(responseBuffer, 0, responseBytes);

                        while ((responseBytes = serverStream.Read(responseBuffer, 0, responseBuffer.Length)) > 0)
                        {
                            clientStream.Write(responseBuffer, 0, responseBytes);
                        }
                    }

                    server.Close();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Ошибка] Обработка запроса: " + ex.Message);
                }
                finally
                {
                    client.Close();
                }
            }
        }

        private static bool TestConnection(string host, int port)
        {
            try
            {
                using (TcpClient tcpClient = new TcpClient())
                {
                    tcpClient.Connect(host, port);
                    Console.WriteLine($"[INFO] Успешное подключение к серверу {host}:{port}");
                    return true; 
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Ошибка подключения к {host}:{port} - {ex.Message}");
                return false;
            }
        }

    }
}
