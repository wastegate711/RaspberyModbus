using System;
using System.Threading.Tasks;

namespace RaspberryModbus
{
    public class SpkDrive
    {
        // Управление / состояние
        const ushort REG_SETPOINT = 0x1000; // уставка, % от P0.10, 10000 = 100.00 %
        const ushort REG_CMD = 0x2000; // команды
        const ushort REG_STATE = 0x3000; // 1 вперёд / 2 назад / 3 стоп
        const ushort REG_FAULT = 0x8000; // код ошибки
        const ushort REG_D0 = 0x7000; // d0.00.. (только чтение)

        // Параметры в ОЗУ (без износа EEPROM): P0.xx -> 00xx
        const ushort RAM_P0_10 = 0x000A; // максимальная частота, 0.01 Гц
        const ushort RAM_P0_17 = 0x0011; // время ускорения 1
        const ushort RAM_P0_18 = 0x0012; // время торможения 1
        const ushort RAM_P0_19 = 0x0013; // единица времени: 0 = 1 с, 1 = 0.1 с, 2 = 0.01 с

        // Команды регистра 2000H
        const ushort CMD_FWD = 1, CMD_REV = 2, CMD_COAST = 5, CMD_DECEL_STOP = 6, CMD_RESET = 7;

        readonly ModbusRtu _mb;
        readonly byte _id;
        double _maxHz = 50.0;
        double _timeStep = 0.1;

        public double CurrentScale = 0.01; // Pd.06 = 0 -> 0.01 А; если Pd.06 = 1 поставьте 0.1
        public double MaxHz => _maxHz;

        public SpkDrive(ModbusRtu mb, byte slaveId = 1) 
        {
            _mb = mb; _id = slaveId; 
        }

        // Прочитать P0.10 и P0.19 (нужны для пересчёта частоты и времени)
        public async Task InitAsync()
        {
            _maxHz = (await _mb.ReadAsync(_id, RAM_P0_10, 1))[0] / 100.0;
            int unit = (await _mb.ReadAsync(_id, RAM_P0_19, 1))[0];
            _timeStep = unit == 0 ? 1.0 : unit == 1 ? 0.1 : 0.01;
        }

        public Task RunForwardAsync() => _mb.WriteAsync(_id, REG_CMD, CMD_FWD);
        public Task RunReverseAsync() => _mb.WriteAsync(_id, REG_CMD, CMD_REV);
        public Task StopAsync() => _mb.WriteAsync(_id, REG_CMD, CMD_DECEL_STOP); // с торможением по P0.18
        public Task CoastStopAsync() => _mb.WriteAsync(_id, REG_CMD, CMD_COAST);      // выбегом
        public Task ResetFaultAsync() => _mb.WriteAsync(_id, REG_CMD, CMD_RESET);

        public Task SetFrequencyAsync(double hz)
        {
            if (hz < 0) hz = 0;
            if (hz > _maxHz) hz = _maxHz;
            ushort v = (ushort)Math.Round(hz / _maxHz * 10000.0);
            return _mb.WriteAsync(_id, REG_SETPOINT, v);
        }

        // Время торможения 1 (P0.18), секунды. Можно менять во время работы (параметр ☆).
        public Task SetDecelSecondsAsync(double sec)
        {
            double raw = Math.Round(sec / _timeStep);
            if (raw < 1) raw = 1;
            if (raw > 65000) raw = 65000;
            return _mb.WriteAsync(_id, RAM_P0_18, (ushort)raw);
        }

        public async Task<double> GetDecelSecondsAsync()
            => (await _mb.ReadAsync(_id, RAM_P0_18, 1))[0] * _timeStep;

        public Task SetAccelSecondsAsync(double sec)
        {
            double raw = Math.Max(1, Math.Min(65000, Math.Round(sec / _timeStep)));
            return _mb.WriteAsync(_id, RAM_P0_17, (ushort)raw);
        }

        public async Task<DriveStatus> ReadStatusAsync()
        {
            var st = (await _mb.ReadAsync(_id, REG_STATE, 1))[0];
            var d = await _mb.ReadAsync(_id, REG_D0, 7);
            var f = (await _mb.ReadAsync(_id, REG_FAULT, 1))[0];
            return new DriveStatus
            {
                State = st,
                FreqHz = d[0] / 100.0,
                SetHz = d[1] / 100.0,
                DcBusV = d[2] / 10.0,
                OutVoltage = d[3],
                CurrentA = d[4] * CurrentScale,
                PowerKw = d[5] / 10.0,
                TorquePct = (short)d[6] / 10.0,
                Fault = f
            };
        }

        public static string FaultText(int code)
        {
            switch (code)
            {
                case 0x00: return "Нет ошибок";
                case 0x02: return "Перегрузка по току при ускорении";
                case 0x03: return "Перегрузка по току при торможении";
                case 0x04: return "Перегрузка по току на постоянной скорости";
                case 0x05: return "Перенапряжение при ускорении";
                case 0x06: return "Перенапряжение при торможении";
                case 0x07: return "Перенапряжение на постоянной скорости";
                case 0x08: return "Сбой питания цепей управления";
                case 0x09: return "Пониженное напряжение";
                case 0x0A: return "Перегрузка преобразователя";
                case 0x0B: return "Перегрузка двигателя";
                case 0x0C: return "Потеря фазы на входе";
                case 0x0D: return "Потеря фазы на выходе";
                case 0x0E: return "Перегрев силового модуля";
                case 0x0F: return "Внешняя ошибка";
                case 0x10: return "Ошибка связи (сторожевой таймер)";
                case 0x11: return "Неисправность контактора";
                case 0x12: return "Ошибка датчиков тока";
                case 0x13: return "Ошибка автонастройки";
                case 0x14: return "Неисправность энкодера";
                case 0x15: return "Ошибка EEPROM";
                case 0x16: return "Неисправность аппаратной части";
                case 0x17: return "Замыкание на землю";
                case 0x1E: return "Недопустимо малая нагрузка";
                case 0x2D: return "Перегрев двигателя";
                default: return "Ошибка 0x" + code.ToString("X2") + " (см. руководство)";
            }
        }
    }
}