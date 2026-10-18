using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;

namespace RaspberryModbus
{
    public class ModbusRtu:IDisposable
    {
        SerialDevice _port;
        readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public void Dispose()
        {
            _port?.Dispose(); 
            _port = null; 
        }

        /// <summary>
        /// Открывает порт с указанными параметрами.
        /// </summary>
        /// <param name="baud">Скорость</param>
        /// <param name="parity">Четность</param>
        /// <param name="stop">Количество стоп бит</param>
        public async Task OpenAsync(uint baud = 9600,
            SerialParity parity = SerialParity.None,
            SerialStopBitCount stop = SerialStopBitCount.Two) // Pd.01 = 0 -> 8-N-2
        {
            string sel = SerialDevice.GetDeviceSelectorFromUsbVidPid(0x0403, 0x6001);
            var devs = await DeviceInformation.FindAllAsync(sel);
            if (devs.Count == 0)
                throw new InvalidOperationException("FT232 (0403:6001) не найден");

            _port = await SerialDevice.FromIdAsync(devs.First().Id);

            if (_port == null)
                throw new InvalidOperationException("Не удалось открыть порт (проверьте манифест)");
            _port.BaudRate = baud;
            _port.DataBits = 8;
            _port.Parity = parity;
            _port.StopBits = stop;
            _port.Handshake = SerialHandshake.None;
            _port.ReadTimeout = TimeSpan.FromMilliseconds(150);
            _port.WriteTimeout = TimeSpan.FromMilliseconds(150);
        }

        /// <summary>
        /// Считает контрольную сумму. Полином 0xA001
        /// </summary>
        /// <param name="d">Данные</param>
        /// <param name="len">Длина</param>
        /// <returns>Возвращает crc16</returns>
        static ushort Crc16(byte[] d, int len)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < len; i++)
            {
                crc ^= d[i];
                for (int b = 0; b < 8; b++)
                    crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
            return crc;
        }

        // 0x06: запись одного регистра
        public async Task WriteAsync(byte slave, ushort addr, ushort value)
        {
            var body = new byte[] { slave, 0x06, (byte)(addr >> 8), (byte)addr, (byte)(value >> 8), (byte)value };
            await TransferAsync(body, 8);
        }

        private async Task<byte[]> TransferAsync(byte[] body, int expectedLen)
        {
            await _lock.WaitAsync();
            try
            {
                var frame = new byte[body.Length + 2];
                Array.Copy(body, frame, body.Length);
                ushort crc = Crc16(body, body.Length);
                frame[body.Length] = (byte)(crc & 0xFF);      // CRC: младший байт первым
                frame[body.Length + 1] = (byte)(crc >> 8);

                var w = new DataWriter(_port.OutputStream);
                w.WriteBytes(frame);
                await w.StoreAsync();
                w.DetachStream();

                var r = new DataReader(_port.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
                try
                {
                    var head = await ReadExactAsync(r, 2);
                    int len = (head[1] & 0x80) != 0 ? 5 : expectedLen;
                    var rest = await ReadExactAsync(r, len - 2);
                    var resp = new byte[len];
                    Array.Copy(head, resp, 2);
                    Array.Copy(rest, 0, resp, 2, len - 2);

                    if (Crc16(resp, len - 2) != (ushort)(resp[len - 2] | (resp[len - 1] << 8)))
                        throw new InvalidOperationException("Ошибка CRC ответа");
                    if ((resp[1] & 0x80) != 0)
                        throw new InvalidOperationException("Modbus-исключение, код " + resp[2]);
                    return resp;
                }
                catch
                {
                    r.DetachStream();
                    await DrainAsync();
                    throw;
                }
                finally
                {
                    await Task.Delay(8); // пауза >= 3.5 символа между кадрами
                }
            }
            finally { _lock.Release(); }
        }

        public async Task<ushort[]> ReadAsync(byte slave, ushort addr, ushort count)
        {
            if (count < 1 || count > 12) 
                throw new ArgumentOutOfRangeException(nameof(count));
            var body = new byte[] { slave, 0x03, (byte)(addr >> 8), (byte)addr, (byte)(count >> 8), (byte)count };
            var resp = await TransferAsync(body, 5 + count * 2);
            var res = new ushort[count];
            for (int i = 0; i < count; i++)
                res[i] = (ushort)((resp[3 + i * 2] << 8) | resp[4 + i * 2]);
            return res;
        }

        private async Task DrainAsync()
        {
            var old = _port.ReadTimeout;
            _port.ReadTimeout = TimeSpan.FromMilliseconds(20);
            try
            {
                var r = new DataReader(_port.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
                while (await r.LoadAsync(64) > 0) 
                { 
                    r.ReadBuffer(r.UnconsumedBufferLength); 
                }
                r.DetachStream();
            }
            catch { }
            _port.ReadTimeout = old;
        }

        private async Task<byte[]> ReadExactAsync(DataReader r, int n)
        {
            var buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                uint k = await r.LoadAsync((uint)(n - got));
                if (k == 0)
                    throw new TimeoutException("Нет ответа от преобразователя");
                var chunk = new byte[k];
                r.ReadBytes(chunk);
                Array.Copy(chunk, 0, buf, got, (int)k);
                got += (int)k;
            }
            return buf;
        }
    }
}