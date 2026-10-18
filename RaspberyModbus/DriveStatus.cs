namespace RaspberryModbus
{
    public struct DriveStatus
    {
        public int State;          // 1 вперёд, 2 назад, 3 стоп (регистр 3000H)
        public double FreqHz;      // d0.00
        public double SetHz;       // d0.01
        public double DcBusV;      // d0.02
        public double OutVoltage;  // d0.03
        public double CurrentA;    // d0.04
        public double PowerKw;     // d0.05
        public double TorquePct;   // d0.06
        public int Fault;          // регистр 8000H
    }
}