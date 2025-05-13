using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic; // Для List<IPAddress>

public class SimpleTraceroute
{
    // Типы ICMP-сообщений
    private const byte ICMP_ECHO_REQUEST_TYPE = 8;    // Эхо-запрос
    private const byte ICMP_ECHO_REQUEST_CODE = 0;
    private const byte ICMP_ECHO_REPLY_TYPE = 0;      // Эхо-ответ
    private const byte ICMP_TIME_EXCEEDED_TYPE = 11;  // Время жизни пакета истекло
    private const byte ICMP_DEST_UNREACHABLE_TYPE = 3; // Узел назначения недостижим


    private const int MAX_HOPS = 30;                 // Максимальное количество узлов (прыжков)
    private const int TIMEOUT_MS = 1000;             // Таймаут ожидания ответа в миллисекундах (1 секунда)
    private const int NUM_PROBES = 3;                // Количество зондирующих пакетов на каждый TTL
    // Заголовок ICMP: Тип (1) + Код (1) + Контрольная сумма (2) + Идентификатор (2) + Порядковый номер (2) = 8 байт
    private const int ICMP_MIN_HEADER_SIZE = 8;
    // Наша простая полезная нагрузка будет 4 байта для временной метки
    private const int PAYLOAD_SIZE = 4;


    public static void Main(string[] args)
    {
        string destInput = null; // Введенное пользователем имя хоста или IP

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

        IPAddress destAddress = null; // IP-адрес назначения

        // Пытаемся разобрать введенную строку как IP-адрес
        if (!IPAddress.TryParse(destInput, out destAddress))
        {
            Console.WriteLine($"Попытка разрешить имя хоста: {destInput}...");
            try
            {
                IPHostEntry hostEntry = Dns.GetHostEntry(destInput);
                bool foundIpV4 = false;
                foreach (IPAddress ip in hostEntry.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork) // Предпочитаем IPv4
                    {
                        destAddress = ip;
                        foundIpV4 = true;
                        break;
                    }
                }
                if (!foundIpV4) // Если IPv4 не найден, берем первый из списка (может быть IPv6)
                {
                    if (hostEntry.AddressList.Length > 0)
                    {
                        destAddress = hostEntry.AddressList[0];
                        Console.WriteLine($"Внимание: IPv4-адрес для {destInput} не найден. Используется первый разрешенный адрес: {destAddress} ({destAddress.AddressFamily})");
                        // Примечание: Этот простой traceroute разработан для IPv4. Для IPv6 потребовался бы ProtocolType.IcmpV6 и другая обработка.
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
            catch (Exception ex) // Перехват других возможных исключений от Dns.GetHostEntry
            {
                Console.WriteLine($"Произошла непредвиденная ошибка при разрешении имени хоста '{destInput}': {ex.Message}");
                return;
            }
        }
        else // Если строка успешно разобрана как IP-адрес
        {
            if (destAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                Console.WriteLine("Эта версия traceroute поддерживает только IPv4-адреса.");
                return;
            }
            Console.WriteLine($"Трассировка маршрута к {destInput} [{destAddress}]");
        }


        Console.WriteLine($"с максимальным количеством прыжков {MAX_HOPS}:\n");

        // Используем ID процесса для ICMP Identifier, чтобы сделать пакеты относительно уникальными
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
                // Использование "сырого" сокета требует прав администратора
                using (Socket rawSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
                {
                    // Устанавливаем TTL для исходящих пакетов
                    rawSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
                    // Устанавливаем таймаут на получение ответа
                    rawSocket.ReceiveTimeout = TIMEOUT_MS;

                    // Конечная точка назначения для SendTo (порт не имеет значения для "сырого" ICMP)
                    IPEndPoint destEndPoint = new IPEndPoint(destAddress, 0);

                    // Формируем ICMP Echo Request пакет
                    // Заголовок ICMP (8 байт) + Полезная нагрузка (например, 4 байта для временной метки)
                    byte[] icmpPacket = new byte[ICMP_MIN_HEADER_SIZE + PAYLOAD_SIZE];

                    icmpPacket[0] = ICMP_ECHO_REQUEST_TYPE; // Тип
                    icmpPacket[1] = ICMP_ECHO_REQUEST_CODE; // Код
                    icmpPacket[2] = 0; // Старший байт контрольной суммы (будет вычислен)
                    icmpPacket[3] = 0; // Младший байт контрольной суммы (будет вычислен)

                    // Идентификатор (в сетевом порядке байт)
                    Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)packetId)), 0, icmpPacket, 4, 2);

                    // Порядковый номер (в сетевом порядке байт) - делаем его уникальным для каждой пробы
                    // Максимальный TTL 255, Максимальное количество проб 255
                    ushort sequenceNumber = (ushort)(((ttl & 0xFF) << 8) | (probe & 0xFF));
                    Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)sequenceNumber)), 0, icmpPacket, 6, 2);

                    // Полезная нагрузка: простая 32-битная временная метка (миллисекунды, младшая часть)
                    int payloadTimestamp = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0xFFFFFFFF);
                    Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payloadTimestamp)), 0, icmpPacket, 8, 4);

                    // Вычисляем контрольную сумму ICMP (для заголовка ICMP + данных)
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

                        // IP-адрес узла, от которого пришел ответ
                        // Определяем только один раз для текущего TTL, чтобы не делать лишних запросов DNS
                        if (!hopReportedForCurrentTTL)
                        {
                            currentHopAddress = ((IPEndPoint)remoteEP).Address;
                            hopReportedForCurrentTTL = true;
                        }


                        // Полученные данные - это полный IP-пакет. Данные ICMP начинаются после IP-заголовка.
                        // Длина IP-заголовка находится в младших 4 битах первого байта IP-заголовка, умноженных на 4.
                        int outerIpHeaderLength = (receiveBuffer[0] & 0x0F) * 4;
                        byte receivedIcmpType = receiveBuffer[outerIpHeaderLength]; // Тип полученного ICMP-сообщения
                        // byte receivedIcmpCode = receiveBuffer[outerIpHeaderLength + 1]; // Код (пока не используется напрямую)

                        ushort receivedId = 0;  // Идентификатор из полученного пакета
                        ushort receivedSeq = 0; // Порядковый номер из полученного пакета

                        if (receivedIcmpType == ICMP_TIME_EXCEEDED_TYPE) // Если TTL истек
                        {
                            // В сообщении "Time Exceeded" инкапсулирован исходный IP-заголовок + 8 байт исходных данных (наш ICMP-заголовок)
                            // Смещение: ВнешнийIPЗаголовок + ICMPЗаголовокTimeExceeded(8 байт) + ВнутреннийIPЗаголовок
                            int innerIpHeaderStartOffset = outerIpHeaderLength + ICMP_MIN_HEADER_SIZE;
                            if (bytesRead > innerIpHeaderStartOffset) // Проверяем, достаточно ли данных для внутреннего IP-заголовка
                            {
                                int innerIpHeaderLength = (receiveBuffer[innerIpHeaderStartOffset] & 0x0F) * 4;
                                int originalIcmpEchoHeaderOffset = innerIpHeaderStartOffset + innerIpHeaderLength;

                                // Извлекаем ID и Seq из инкапсулированного ICMP Echo Request
                                if (bytesRead >= originalIcmpEchoHeaderOffset + ICMP_MIN_HEADER_SIZE)
                                {
                                    receivedId = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(receiveBuffer, originalIcmpEchoHeaderOffset + 4));
                                    receivedSeq = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(receiveBuffer, originalIcmpEchoHeaderOffset + 6));
                                }
                            }
                        }
                        else if (receivedIcmpType == ICMP_ECHO_REPLY_TYPE) // Если получен эхо-ответ
                        {
                            // Прямой эхо-ответ, заголовок ICMP идет сразу после IP-заголовка
                            if (bytesRead >= outerIpHeaderLength + ICMP_MIN_HEADER_SIZE)
                            {
                                receivedId = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(receiveBuffer, outerIpHeaderLength + 4));
                                receivedSeq = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(receiveBuffer, outerIpHeaderLength + 6));
                                destinationReached = true; // Мы достигли цели
                            }
                        }
                        else if (receivedIcmpType == ICMP_DEST_UNREACHABLE_TYPE) // Если узел назначения недостижим
                        {
                            // Аналогично Time Exceeded, Destination Unreachable также инкапсулирует информацию об исходном пакете
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
                            // Мы "достигли" сущности, которая сообщает о недостижимости (часто сам узел назначения или брандмауэр).
                            destinationReached = true; // Считаем это концом трассировки для данного пути.
                        }


                        // Проверяем, соответствует ли ответ нашему отправленному пакету
                        if (receivedId != packetId || receivedSeq != sequenceNumber)
                        {
                            // Ответ не для нашего конкретного пакета (возможно, запоздалый ответ от предыдущей пробы).
                            // Отмечаем как '?' для этой пробы, но IP-адрес узла у нас может быть.
                            Console.Write($"  ?    ");
                        }
                        // Если ID и Seq совпадают, RTT уже был добавлен в rttList.
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        rttList.Add(-1); // Используем -1 для обозначения таймаута для этой пробы
                    }
                    catch (Exception ex) // Другие ошибки сокета при получении
                    {
                        rttList.Add(-2); // Используем -2 для обозначения других ошибок
                        Debug.WriteLine($"Ошибка пробы (TTL {ttl}, Проба {probe}): {ex.GetType().Name} - {ex.Message}");
                    }
                } // Конец блока using rawSocket
            } // Конец цикла проб (probe loop)

            // Выводим результаты для текущего TTL
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
                    // Попытка разрешить IP-адрес в имя хоста. Это может быть медленно.
                    // В системных утилитах traceroute это часто опционально или выполняется в конце.
                    // IPHostEntry entry = Dns.GetHostEntry(currentHopAddress);
                    // hostName = $" ({entry.HostName})";
                }
                catch (SocketException) { /* Игнорируем, если не удается разрешить */ }
                Console.WriteLine($"  {currentHopAddress}{hostName}");
            }
            else if (rttList.Count > 0 && rttList.TrueForAll(r => r < 0)) // Все пробы для этого TTL завершились таймаутом или ошибкой
            {
                Console.WriteLine("  Превышен интервал ожидания для запроса.");
            }
            else if (rttList.Count == 0) // Не должно произойти, если NUM_PROBES > 0
            {
                Console.WriteLine("  Для этого узла не было отправлено проб.");
            }


            if (destinationReached) // Если достигнут конечный узел
            {
                Console.WriteLine("\nТрассировка завершена.");
                break; // Выходим из основного цикла TTL
            }
            if (ttl == MAX_HOPS) // Если достигнуто максимальное количество прыжков
            {
                Console.WriteLine("\nДостигнуто максимальное количество прыжков.");
            }

        } // Конец цикла TTL (ttl loop)
    }

    /// <summary>
    /// Вычисляет контрольную сумму ICMP.
    /// Массив байтов 'data' должен содержать ICMP-сообщение (заголовок и полезную нагрузку).
    /// Поле контрольной суммы в 'data' (смещение + 2 и смещение + 3) ДОЛЖНО быть равно нулю перед вызовом этого метода.
    /// </summary>
    /// <param name="data">Массив байтов, содержащий ICMP-пакет (заголовок + данные).</param>
    /// <param name="offset">Начальное смещение ICMP-сообщения в массиве.</param>
    /// <param name="length">Длина ICMP-сообщения (заголовок + данные).</param>
    /// <returns>16-битное значение контрольной суммы в представлении сетевого порядка байтов.</returns>
    private static ushort CalculateChecksum(byte[] data, int offset, int length)
    {
        uint sum = 0;
        int i = offset;

        // Суммируем 16-битные слова
        while (length > 1)
        {
            // Объединяем два байта в ushort (в порядке big-endian, т.к. data[i] - старший байт)
            sum += (ushort)((data[i++] << 8) | data[i++]);
            length -= 2;
        }

        // Добавляем оставшийся байт, если длина нечетная, дополняем нулевым байтом
        // (как будто это data[i] << 8)
        if (length > 0)
        {
            sum += (uint)(data[i] << 8);
        }

        // Сворачиваем 32-битную сумму в 16-битную: добавляем перенос к результату
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        // Обратный код (one's complement)
        // Результат уже является контрольной суммой в формате сетевого порядка байтов
        return (ushort)~sum;
    }
}