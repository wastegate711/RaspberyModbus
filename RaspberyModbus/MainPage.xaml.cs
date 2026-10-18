using System;
using System.Globalization;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

// Документацию по шаблону элемента "Пустая страница" см. по адресу https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x419

namespace RaspberryModbus
{
    /// <summary>
    /// Пустая страница, которую можно использовать саму по себе или для перехода внутри фрейма.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        readonly ModbusRtu _mb = new ModbusRtu();
        SpkDrive _drive;
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        bool _polling;
        int _failures;

        public MainPage()
        {
            this.InitializeComponent();
        }

        async Task ConnectAsync()
        {
            try
            {
                await _mb.OpenAsync(9600);              // Pd.00: 9600, Pd.01 = 0 -> 8-N-2
                _drive = new SpkDrive(_mb, 1);          // Pd.02 = 1
                await _drive.InitAsync();
                await UpdateDecelLabelAsync();
                TbLog.Text = "";
                _timer.Start();
            }
            catch (Exception ex)
            {
                TbLog.Text = "Подключение: " + ex.Message;
            }
        }

        async Task PollAsync()
        {
            if (_polling || _drive == null)
                return;

            _polling = true;

            try
            {
                var s = await _drive.ReadStatusAsync();
                _failures = 0;
                TbState.Text = s.State == 1 ? "Вперёд" : s.State == 2 ? "Назад" : "Стоп";
                TbFreq.Text = s.FreqHz.ToString("0.00", CultureInfo.InvariantCulture);
                TbCurrent.Text = s.CurrentA.ToString("0.00", CultureInfo.InvariantCulture);
                TbDc.Text = s.DcBusV.ToString("0", CultureInfo.InvariantCulture);

                bool fault = s.Fault != 0;
                FaultBar.Background = new SolidColorBrush(fault ? Color.FromArgb(255, 160, 32, 32)
                    : Color.FromArgb(255, 58, 58, 58));
                TbFault.Text = fault ? "АВАРИЯ: " + SpkDrive.FaultText(s.Fault) : "Нет ошибок";
            }
            catch (Exception ex)
            {
                if (++_failures >= 3)
                {
                    TbFault.Text = "Нет связи";
                    TbLog.Text = ex.Message;
                }
            }
            finally { _polling = false; }
        }

        // ---- вспомогательное ----
        static bool TryParse(string text, out double v)
            => double.TryParse((text ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        async Task Do(Func<Task> action)
        {
            if (_drive == null) { TbLog.Text = "Нет подключения"; return; }
            try { await action(); TbLog.Text = ""; }
            catch (Exception ex) { TbLog.Text = ex.Message; }
        }

        async Task UpdateDecelLabelAsync()
        {
            double t = await _drive.GetDecelSecondsAsync();
            TbDecelActive.Text = "Активное время торможения: " + t.ToString("0.0#", CultureInfo.InvariantCulture) + " с";
        }

        // ---- обработчики кнопок ----
        async void OnForward(object s, RoutedEventArgs e) => await Do(() => _drive.RunForwardAsync());
        async void OnReverse(object s, RoutedEventArgs e) => await Do(() => _drive.RunReverseAsync());
        async void OnStop(object s, RoutedEventArgs e) => await Do(() => _drive.StopAsync());
        async void OnCoast(object s, RoutedEventArgs e) => await Do(() => _drive.CoastStopAsync());
        async void OnReset(object s, RoutedEventArgs e) => await Do(() => _drive.ResetFaultAsync());

        async void OnSetFreq(object s, RoutedEventArgs e)
        {
            if (!TryParse(TxtFreq.Text, out double hz)) { TbLog.Text = "Неверная частота"; return; }
            await Do(() => _drive.SetFrequencyAsync(hz));
        }


        async Task ApplyDecel(string text)
        {
            if (!TryParse(text, out double sec) || sec <= 0) { TbLog.Text = "Неверное время"; return; }
            await Do(async () =>
            {
                await _drive.SetDecelSecondsAsync(sec);
                await UpdateDecelLabelAsync(); // читаем обратно и показываем фактическое значение
            });
        }

        async void OnDecelNormal(object s, RoutedEventArgs e) => await ApplyDecel(TxtDecelNormal.Text);
        async void OnDecelThread(object s, RoutedEventArgs e) => await ApplyDecel(TxtDecelThread.Text);
    }
}
