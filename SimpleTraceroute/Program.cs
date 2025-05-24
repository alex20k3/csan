using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;

public class SimpleTraceroute
{
    // Типы ICMP-сообщений
    private const byte ICMP_ECHO_REQUEST_TYPE = 8;    // Эхо-запрос
    private const byte ICMP_ECHO_REQUEST_CODE = 0;
    private const byte ICMP_ECHO_REPLY_TYPE = 0;      // Эхо-ответ
    private const byte ICMP_TIME_EXCEEDED_TYPE = 11;  // Время жизни пакета истекло
    private const byte ICMP_DEST_UNREACHABLE_TYPE = 3; // Узел назначения недостижим


    private const int MAX_HOPS = 30;                 
    private const int TIMEOUT_MS = 1000;             
    private const int NUM_PROBES = 3;                
    private const int ICMP_MIN_HEADER_SIZE = 8;
    private const int PAYLOAD_SIZE = 4;


    public static void Main(string[] args)
    {
        string destInput = null; 

        if (args.Length > 0)
        {
            destInput = args[0];
            Console.WriteLine($"Используется назначение из аргумента командной строки: {destInput}");
        }
        else
        {
            Console.Write("Введите IP-адрес или имя хоста назначения: ");
            destInput = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(destInput))
            {
                Console.WriteLine("Назначение не указано. Выход.");
                return;
            }
        }

        IPAddress destAddress = null; 

        if (!IPAddress.TryParse(destInput, out destAddress))
        {
            Console.WriteLine($"Попытка разрешить имя хоста: {destInput}...");
            try
            {
                IPHostEntry hostEntry = Dns.GetHostEntry(destInput);
                bool foundIpV4 = false;
                foreach (IPAddress ip in hostEntry.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork) 
                    {
                        destAddress = ip;
                        foundIpV4 = true;
                        break;
                    }
                }
                if (!foundIpV4) 
                {
                    if (hostEntry.AddressList.Length > 0)
                    {
                        destAddress = hostEntry.AddressList[0];
                        Console.WriteLine($"Внимание: IPv4-адрес для {destInput} не найден. Используется первый разрешенный адрес: {destAddress} ({destAddress.AddressFamily})");
                        if (destAddress.AddressFamily != AddressFamily.InterNetwork)
                        {
                            Console.WriteLine("Эта версия traceroute поддерживает только IPv4-адреса назначения.");
                            return;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Не удалось разрешить имя хоста '{destInput}' в IP-адрес.");
                        return;
                    }
                }
                Console.WriteLine($"Трассировка маршрута к {destInput} [{destAddress}]");
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"Ошибка при разрешении имени хоста '{destInput}': {ex.Message}");
                return;
            }
            catch (Exception ex) 
            {
                Console.WriteLine($"Произошла непредвиденная ошибка при разрешении имени хоста '{destInput}': {ex.Message}");
                return;
            }
        }
        else 
        {
            if (destAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                Console.WriteLine("Эта версия traceroute поддерживает только IPv4-адреса.");
                return;
            }
            Console.WriteLine($"Трассировка маршрута к {destInput} [{destAddress}]");
        }


        Console.WriteLine($"с максимальным количеством прыжков {MAX_HOPS}:\n");

        ushort packetId = (ushort)(Process.GetCurrentProcess().Id & 0xFFFF);

        for (int ttl = 1; ttl <= MAX_HOPS; ttl++)
        {
            Console.Write($"{ttl,2}  ");
            IPAddress currentHopAddress = null;       // IP-адрес текущего узла на маршруте
            bool hopReportedForCurrentTTL = false; // Флаг, что адрес узла для текущего TTL уже был получен
            bool destinationReached = false;         // Флаг, что достигнут конечный узел
            List<long> rttList = new List<long>();   // Список времен отклика (RTT) для текущего TTL


            for (int probe = 0; probe < NUM_PROBES; probe++)
            {
               
                using (Socket rawSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
                {
                  
                    rawSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
                  
                    rawSocket.ReceiveTimeout = TIMEOUT_MS;

                    
                    IPEndPoint destEndPoint = new IPEndPoint(destAddress, 0);

                    byte[] icmpPacket = new byte[ICMP_MIN_HEADER_SIZE + PAYLOAD_SIZE];

                    icmpPacket[0] = ICMP_ECHO_REQUEST_TYPE; 
                    icmpPacket[1] = ICMP_ECHO_REQUEST_CODE; 
                    icmpPacket[2] = 0; 
                    icmpPacket[3] = 0; 

                  
                    Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)packetId)), 0, icmpPacket, 4, 2);

                    ushort sequenceNumber = (ushort)(((ttl & 0xFF) << 8) | (probe & 0xFF));
                    Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)sequenceNumber)), 0, icmpPacket, 6, 2);

                    int payloadTimestamp = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0xFFFFFFFF);
                    Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payloadTimestamp)), 0, icmpPacket, 8, 4);

                    ushort checksum = CalculateChecksum(icmpPacket, 0, icmpPacket.Length);
                    icmpPacket[2] = (byte)(checksum >> 8);   // Старший байт контрольной суммы
                    icmpPacket[3] = (byte)(checksum & 0xFF); // Младший байт контрольной суммы

                    byte[] receiveBuffer = new byte[2048]; // Буфер для получения полного IP-пакета
                    EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0); // Откуда пришел ответ
                    Stopwatch stopwatch = new Stopwatch();   // Для измерения RTT

                    try
                    {
                        rawSocket.SendTo(icmpPacket, destEndPoint); // Отправляем пакет
                        stopwatch.Start();                          // Начинаем отсчет времени

                        int bytesRead = rawSocket.ReceiveFrom(receiveBuffer, ref remoteEP); // Ожидаем ответ
                        stopwatch.Stop();                                                   // Останавливаем отсчет
                        rttList.Add(stopwatch.ElapsedMilliseconds);                         // Сохраняем RTT

                       
                        if (!hopReportedForCurrentTTL)
                        {
                            currentHopAddress = ((IPEndPoint)remoteEP).Address;
                            hopReportedForCurrentTTL = true;
                        }


                       
                        int outerIpHeaderLength = (receiveBuffer[0] & 0x0F) * 4;
                        byte receivedIcmpType = receiveBuffer[outerIpHeaderLength]; 

                        ushort receivedId = 0;  
                        ushort receivedSeq = 0; 

                        if (receivedIcmpType == ICMP_TIME_EXCEEDED_TYPE) // Если TTL истек
                        {
                            
                            int innerIpHeaderStartOffset = outerIpHeaderLength + ICMP_MIN_HEADER_SIZE;
                            if (bytesRead > innerIpHeaderStartOffset) // Проверяем, достаточно ли данных для внутреннего IP-заголовка
                            {
                                int innerIpHeaderLength = (receiveBuffer[innerIpHeaderStartOffset] & 0x0F) * 4;
                                int originalIcmpEchoHeaderOffset = innerIpHeaderStartOffset + innerIpHeaderLength;

                                
                                if (bytesRead >= originalIcmpEchoHeaderOffset + ICMP_MIN_HEADER_SIZE)
                                {
                                    receivedId = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(receiveBuffer, originalIcmpEchoHeaderOffset + 4));
                                    receivedSeq = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(receiveBuffer, originalIcmpEchoHeaderOffset + 6));
                                }
                            }
                        }
                        else if (receivedIcmpType == ICMP_ECHO_REPLY_TYPE) // Если получен эхо-ответ
                        {
                           
                            if (bytesRead >= outerIpHeaderLength + ICMP_MIN_HEADER_SIZE)
                            {
                                receivedId = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(receiveBuffer, outerIpHeaderLength + 4));
                                receivedSeq = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(receiveBuffer, outerIpHeaderLength + 6));
                                destinationReached = true; 
                            }
                        }
                        else if (receivedIcmpType == ICMP_DEST_UNREACHABLE_TYPE) // Если узел назначения недостижим
                        {
                            int innerIpHeaderStartOffset = outerIpHeaderLength + ICMP_MIN_HEADER_SIZE;
                            if (bytesRead > innerIpHeaderStartOffset)
                            {
                                int innerIpHeaderLength = (receiveBuffer[innerIpHeaderStartOffset] & 0x0F) * 4;
                                int originalIcmpEchoHeaderOffset = innerIpHeaderStartOffset + innerIpHeaderLength;
                                if (bytesRead >= originalIcmpEchoHeaderOffset + ICMP_MIN_HEADER_SIZE)
                                {
                                    receivedId = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(receiveBuffer, originalIcmpEchoHeaderOffset + 4));
                                    receivedSeq = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(receiveBuffer, originalIcmpEchoHeaderOffset + 6));
                                }
                            }
                            destinationReached = true;
                        }


                      
                        if (receivedId != packetId || receivedSeq != sequenceNumber)
                        {
                            Console.Write($"  ?    ");
                        }
                      
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        rttList.Add(-1); 
                    }
                    catch (Exception ex) 
                    {
                        rttList.Add(-2);
                        Debug.WriteLine($"Ошибка пробы (TTL {ttl}, Проба {probe}): {ex.GetType().Name} - {ex.Message}");
                    }
                } 
            } 

            
            foreach (long rtt in rttList)
            {
                if (rtt == -1) Console.Write($"  *    ");        // Таймаут
                else if (rtt == -2) Console.Write($"  !    ");    // Другая ошибка
                else Console.Write($"{rtt,4} ms  ");            // RTT
            }

            if (currentHopAddress != null) // Если был получен IP-адрес узла
            {
                string hostName = "";
                try
                {
                    // Попытка разрешить IP-адрес в имя хоста.
                    IPHostEntry entry = Dns.GetHostEntry(currentHopAddress);
                    hostName = $" ({entry.HostName})";
                }
                catch (SocketException) { /* Игнорируем, если не удается разрешить */ }
                Console.WriteLine($"  {currentHopAddress}{hostName}");
            }
            else if (rttList.Count > 0 && rttList.TrueForAll(r => r < 0)) 
            {
                Console.WriteLine("  Превышен интервал ожидания для запроса.");
            }
            else if (rttList.Count == 0) 
            {
                Console.WriteLine("  Для этого узла не было отправлено проб.");
            }


            if (destinationReached) 
            {
                Console.WriteLine("\nТрассировка завершена.");
                break; 
            }
            if (ttl == MAX_HOPS) 
            {
                Console.WriteLine("\nДостигнуто максимальное количество прыжков.");
            }

        }
    }

    /// <param name="data">Массив байтов, содержащий ICMP-пакет (заголовок + данные).</param>
    /// <param name="offset">Начальное смещение ICMP-сообщения в массиве.</param>
    /// <param name="length">Длина ICMP-сообщения (заголовок + данные).</param>
  
    private static ushort CalculateChecksum(byte[] data, int offset, int length)
    {
        uint sum = 0;
        int i = offset;

      
        while (length > 1)
        {
           
            sum += (ushort)((data[i++] << 8) | data[i++]);
            length -= 2;
        }

       
        if (length > 0)
        {
            sum += (uint)(data[i] << 8);
        }

       
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

      
        return (ushort)~sum;
    }
}