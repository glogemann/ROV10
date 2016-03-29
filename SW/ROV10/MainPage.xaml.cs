using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.Gaming.Input;
using Windows.UI.Xaml.Shapes;
using Windows.UI;
//using Microsoft.Advertising.WinRT.UI;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace ROV10
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private StreamSocket socket = null;
        private DataReader _dr = null;
        private DataWriter _dw = null;
        string ConnectionURL = null;

        private Gamepad controller = null;
        private bool isControllerConfig = false;

        // Sensor Data
        private DeviceData ddata = new DeviceData();


        Windows.Storage.ApplicationDataContainer localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

        #region ALM 
        /// <summary>
        /// 
        /// </summary>
        public MainPage()
        {
            this.InitializeComponent();
            Stick.Visibility = Visibility.Collapsed;
            Settings.Visibility = Visibility.Collapsed;
            GamePadCalibrate.Visibility = Visibility.Collapsed;
            GamePadSettings.Visibility = Visibility.Collapsed; 
            GamePadSwitch.Visibility = Visibility.Collapsed;
            isControllerConfig = false;
            CalibrateButton.Content = "Calibrate";

            // Get the first controller
            try
            {
                var x = Gamepad.Gamepads;
                var x1 = Gamepad.Gamepads.Count;
                Gamepad.GamepadAdded += Gamepad_GamepadAdded;
                if (x1 > 0)
                {
                    controller = Gamepad.Gamepads.First();
                    GamePadSettings.Visibility = Visibility.Visible;
                    GamePadSwitch.IsOn = true;
                }
            }
            catch(Exception e)
            {
                controller = null; 
            }

            // Setup connection URL; 
            ConnectionURL = (string)localSettings.Values["ConnectionURL"];
            if (ConnectionURL == null)
            {
                localSettings.Values["ConnectionURL"] = "192.168.0.43";
                ConnectionURL = (string)localSettings.Values["ConnectionURL"];
            }
            TextBoxConnectionURL.Text = ConnectionURL;
            StatusText.Text = "trying to connect....";
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (StickTimer != null)
            {
                if (StickTimer.IsEnabled)
                {
                    StickTimer.Stop();
                }
                StickTimer = null;
            }
            if (socket != null)
            {
                socket.Dispose();
                socket = null;
            }
        }

        private async void Gamepad_GamepadAdded(object sender, Gamepad e)
        {
            controller = e;

            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                try
                {
                    GamePadSwitch.IsOn = true;
                }
                catch
                {

                }
            }); 

        }

        private void GamePadSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (GamePadSwitch.IsOn)
            {
                if (controller != null)
                {
                    GamePadSettings.Visibility = Visibility.Visible;
                    GamePadSwitch.Visibility = Visibility.Visible;
                    GamePadCalibrate.Visibility = Visibility.Collapsed;
                    GamePadConfig.Visibility = Visibility.Collapsed;
                    isControllerConfig = false;
                    CalibrateButton.Content = "Calibrate";
                }
            }
            else
            {
                GamePadCalibrate.Visibility = Visibility.Collapsed;
                GamePadConfig.Visibility = Visibility.Collapsed;
                isControllerConfig = false;
                CalibrateButton.Content = "Calibrate";
            }
        }


        private void Button_Calibrate(object sender, RoutedEventArgs e)
        {
            Button b = (Button)sender;
            string c = (string)b.Content;
            if(c == "Calibrate")
            {
                b.Content = "Stop Calibrating";
                GamePadConfig.Visibility = Visibility.Visible;
                isControllerConfig = true;
            }
            else
            {
                b.Content = "Calibrate";
                GamePadConfig.Visibility = Visibility.Collapsed;
                isControllerConfig = false;
            }
        }



        DispatcherTimer StickTimer = null;

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            //

            TestParameter.Visibility = Visibility.Collapsed;

            if (await ConnectToSocket(ConnectionURL) == "OK")
            {
                StickTimer = new DispatcherTimer();
                StickTimer.Interval = new TimeSpan(0, 0, 0, 0, 10);
                StickTimer.Tick += StickTimer_Tick;
                StickTimer.Start();
                StatusText.Text = "Connected!";
            }
            else
            {
                StatusText.Text = "Connection failed!";
            }

        }

        private bool readingOK = false;
        private int _S1 = 0;
        private int _S2 = 0;
        private int _S3 = 0;
        private int _S4 = 0;


        private void DrawSlider(int value, Canvas c)
        {
            double h = c.RenderSize.Height;
            double w = c.RenderSize.Width;

            var line = new Line();
            line.Stroke = new SolidColorBrush(Colors.LightBlue);
            line.StrokeThickness = 3;
            line.X1 = w / 2;
            line.X2 = (w / 2) + ((w / 2 - 10) * value / 100);
            line.Y1 = h / 2;
            line.Y2 = h / 2;


            var el = new Ellipse();
            el.Stroke = new SolidColorBrush(Colors.LightCoral);
            el.StrokeThickness = 10;
            el.Width = 20;
            el.Height = 20;
            //Canvas.SetTop(el, w / 2 - 5);
            //Canvas.SetLeft(el, (w / 2) + ((w / 2 + 5) * value / 100));

            Canvas.SetTop(el, h / 2 - 10);
            Canvas.SetLeft(el, ((w / 2) + ((w / 2 - 10 ) * value / 100))-10);
            c.Children.Clear();
            c.Children.Add(el);
            c.Children.Add(line);
        }

        private async void StickTimer_Tick(object sender, object e)
        {
            StickTimer.Stop();

            if (controller != null)
            {
                GamepadReading gamepadreading = controller.GetCurrentReading();

                // Mixer 
                double s1 = gamepadreading.RightThumbstickY - gamepadreading.RightThumbstickX;
                if (s1 > 1) s1 = 1;
                if (s1 < -1) s1 = -1;
                double s2 = gamepadreading.RightThumbstickY + gamepadreading.RightThumbstickX;
                if (s2 > 1) s2 = 1;
                if (s2 < -1) s2 = -1;

                // Mixer 
                double s3 = gamepadreading.LeftThumbstickY - gamepadreading.LeftThumbstickX;
                if (s3 > 1) s3 = 1;
                if (s3 < -1) s3 = -1;
                double s4 = gamepadreading.LeftThumbstickY + gamepadreading.LeftThumbstickX;
                if (s4 > 1) s4 = 1;
                if (s4 < -1) s4 = -1;

                _S1 = (int)(s1 * 100);
                DrawSlider(_S1, S1Canvas);
                _S2 = (int)(s2 * 100);
                DrawSlider(_S2, S2Canvas);
                _S3 = (int)(s3 * 100);
                DrawSlider(_S3, S3Canvas);
                _S4 = (int)(s4 * 100);
                DrawSlider(_S4, S4Canvas);

                readingOK = true;
            }
            await SendFrame();
            StickTimer.Start(); 
        }

        private void Splitter_PaneClosed(SplitView sender, object args)
        {
            Settings.Visibility = Visibility.Collapsed;
        }

        #endregion 

        private async Task<string> ConnectToSocket(string connectionURL)
        {
            string result = "OK";
            try
            {
                socket = new StreamSocket();
                socket.Control.KeepAlive = true;
                socket.Control.NoDelay = true;
                await socket.ConnectAsync(new HostName(connectionURL), "8081");

                _dr = new DataReader(socket.InputStream);
                _dw = new DataWriter(socket.OutputStream);

                var t = Task.Run(async delegate
                {
                    // Send Initial Frame to trigger continious stream. 
                    //await SendFrame();

                    while (true)
                    {
                        int syncstate = 0;
                        byte[] headerbuffer = new byte[5];
                        byte command = 0; 
                        while (syncstate<5)
                        {
                            await _dr.LoadAsync(1);
                            byte b = _dr.ReadByte();
                            switch (syncstate)
                            {
                                case 0:
                                    if (b == 0xfd) syncstate++;
                                    else syncstate = 0; 
                                    break;
                                case 1:
                                    if (b == 0xfd) syncstate++;
                                    else syncstate = 0;
                                    break;
                                case 2:
                                    if (b == 0xfd) syncstate++;
                                    else syncstate = 0;
                                    break;
                                case 3:
                                    if (b == 0x00) syncstate++;
                                    else syncstate = 0;
                                    break;
                                case 4:
                                    if (b == 0x01)
                                    {
                                        command = b; 
                                        syncstate++;
                                    }
                                    else syncstate = 0;
                                    break;

                                default:
                                    syncstate = 0;
                                    break; 
                            }
                        }

                        if (command == 0x01)
                        {
                            // Read first 4 bytes (length of the subsequent string).
                            uint wc = await _dr.LoadAsync(sizeof(Int32));
                            int Width = _dr.ReadInt32();

                            uint hc = await _dr.LoadAsync(sizeof(Int32));
                            int Heigh = _dr.ReadInt32();

                            await _dr.LoadAsync(sizeof(uint));
                            uint jsonstringLength = _dr.ReadUInt32();
                            await _dr.LoadAsync(jsonstringLength);
                            string jsonstring = _dr.ReadString(jsonstringLength);
                            JsonObject j = JsonObject.Parse(jsonstring);
                            try
                            {
                                ddata.deviceType = j.GetNamedString("Type");
                                ddata.deviceVersion = j.GetNamedString("Version");
                                ddata.levelA0 = (int)j.GetNamedNumber("A0");
                                ddata.levelA1 = (int)j.GetNamedNumber("A1");
                                ddata.levelA2 = (int)j.GetNamedNumber("A2");
                                ddata.levelA3 = (int)j.GetNamedNumber("A3");

                                ddata.calacc = (int)j.GetNamedNumber("Calacc");
                                ddata.calgyro = (int)j.GetNamedNumber("Calgyro");
                                ddata.calmag = (int)j.GetNamedNumber("Calmag");
                                ddata.eulerX = (int)j.GetNamedNumber("EulerX");
                                ddata.eulerY = (int)j.GetNamedNumber("EulerY");
                                ddata.eulerZ = (int)j.GetNamedNumber("EulerZ");
                                ddata.temp = (int)j.GetNamedNumber("Temp");
                            }
                            catch (Exception ex)
                            {
                            }

                            await _dr.LoadAsync(sizeof(uint));
                            uint stringLength = _dr.ReadUInt32();
                            uint datacount = await _dr.LoadAsync(stringLength);
                            byte[] buffer = new byte[datacount];
                            _dr.ReadBytes(buffer);

                            //old implementtaion for raw bitmaps: 
                            //IBuffer buffer = _dr.ReadBuffer(datacount);
                            //VideoFrame vf = new VideoFrame(BitmapPixelFormat.Bgra8, Width, Heigh);
                            //SoftwareBitmap sb = vf.SoftwareBitmap;
                            //sb.CopyFromBuffer(buffer);
                            //await sbSource.SetBitmapAsync(sb);
                            //Display it in the Image control
                            //PreviewFrameImage.Source = sbSource;

                            MemoryStream ms = new MemoryStream(buffer);
                            var decoder = await BitmapDecoder.CreateAsync(
                            BitmapDecoder.JpegDecoderId, ms.AsRandomAccessStream());
                            var sbit = await decoder.GetSoftwareBitmapAsync();
                            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, () =>
                            {
                                try
                                {
                                    WriteableBitmap bm = new WriteableBitmap((int)decoder.PixelWidth, (int)decoder.PixelHeight);
                                    sbit.CopyToBuffer(bm.PixelBuffer);

                                    // Display it in the Image control
                                    PreviewFrameImage.Source = bm;

                                    Temp.Text = ddata.temp.ToString() + "°C";
                                    compassTransform.Rotation = (double)((ddata.eulerX + 90) * -1);
                                    compassTransform.TranslateY = MainCanvas.ActualHeight / 2 * 1.5; 
                                    // request new Frame: 
                                    //await SendFrame();
                                }
                                catch (Exception e)
                                {
                                }
                            });
                        }


                    }

                });
                }
            catch (Exception exception)
            {
                // If this is an unknown status it means that the error is fatal and retry will likely fail.
                if (SocketError.GetStatus(exception.HResult) == SocketErrorStatus.Unknown)
                {

                }
                result = "Error: Could not connect";
            }
            return result;
        }

        private async Task SendFrame()
        {
            if (_dw != null)
            {
                JsonObject jsonObject = new JsonObject();

                if (TestModeSwitch.IsOn)
                {
                    jsonObject["S1"] = JsonValue.CreateNumberValue(S1Slider.Value);
                    jsonObject["S2"] = JsonValue.CreateNumberValue(S2Slider.Value);
                    jsonObject["S3"] = JsonValue.CreateNumberValue(S3Slider.Value);
                    jsonObject["S4"] = JsonValue.CreateNumberValue(S4Slider.Value);
                }
                else {
                    if (readingOK == true)
                    {
                        jsonObject["S1"] = JsonValue.CreateNumberValue(_S1);
                        jsonObject["S2"] = JsonValue.CreateNumberValue(_S2);
                        jsonObject["S3"] = JsonValue.CreateNumberValue(_S3);
                        jsonObject["S4"] = JsonValue.CreateNumberValue(_S4);
                    }
                    else
                    {
                        jsonObject["S1"] = JsonValue.CreateNumberValue(Stick.X);
                        jsonObject["S2"] = JsonValue.CreateNumberValue(Stick.Y);
                        jsonObject["S3"] = JsonValue.CreateNumberValue(0);
                        jsonObject["S4"] = JsonValue.CreateNumberValue(0);
                    }
                }
                _dw.WriteString("X");
                _dw.WriteInt32(jsonObject.ToString().Length);
                _dw.WriteString(jsonObject.ToString());
                await _dw.StoreAsync();
            }
        }
        #region OnScreenjoystick

        private void MainCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Windows.UI.Xaml.Input.Pointer ptr = e.Pointer;
            PointerPoint pt = e.GetCurrentPoint(MainCanvas);


            Stick.Visibility = Visibility.Visible;
            Stick.RenderTransformOrigin = new Point(0, 0);

            ScaleTransform myScaleTransform = new ScaleTransform();
            myScaleTransform.ScaleX = MainCanvas.ActualWidth / 500;
            myScaleTransform.ScaleY = MainCanvas.ActualWidth / 500;

            TranslateTransform myTranslateTransfrom = new TranslateTransform();
            myTranslateTransfrom.X = pt.Position.X - (170 * myScaleTransform.ScaleX);
            myTranslateTransfrom.Y = pt.Position.Y - (170 * myScaleTransform.ScaleY);

            TransformGroup myTransformGroup = new TransformGroup();

            myTransformGroup.Children.Add(myScaleTransform);
            myTransformGroup.Children.Add(myTranslateTransfrom);

            Stick.RenderTransform = myTransformGroup;
            Stick.Base_PointerPressed(sender, e);
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (Splitter.IsPaneOpen)
            {
                Splitter.IsPaneOpen = false;
                Settings.Visibility = Visibility.Collapsed;
            }
            else
            {
                Splitter.IsPaneOpen = true;
                Settings.Visibility = Visibility.Visible;

            }
        }

        private async void Button_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "trying to connect....";
            if (socket != null)
            {
                try
                {
                    await socket.CancelIOAsync();
                    _dr.DetachStream();
                    _dw.DetachStream();
                    _dr.Dispose();
                    _dw.Dispose();
                    socket.Dispose();
                }
                catch (Exception ex)
                {

                }

                localSettings.Values["ConnectionURL"] = TextBoxConnectionURL.Text;
                ConnectionURL = (string)localSettings.Values["ConnectionURL"];


                if (await ConnectToSocket(ConnectionURL) == "OK")
                {
                    Splitter.IsPaneOpen = false;
                    Settings.Visibility = Visibility.Collapsed;
                    StatusText.Text = "Connected!";
                }
                else
                {
                    StatusText.Text = "Connection failed!";
                }

            }
        }




        #endregion

        private void TestModeSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (TestModeSwitch.IsOn)
            {
                TestParameter.Visibility = Visibility.Visible;
            }
            else
            {
                TestParameter.Visibility = Visibility.Collapsed;
            }
        }

        private void resetSlider_Click(object sender, RoutedEventArgs e)
        {
            S1Slider.Value = 0;
            S2Slider.Value = 0;
            S3Slider.Value = 0;
            S4Slider.Value = 0; 
        }
    }

    class DeviceData
    {
        private string _deviceVersion = "unknown";
        public string deviceVersion
        {
            get { return _deviceVersion; }
            set { _deviceVersion = value; }
        }

        private string _deviceType = "unknown";
        public string deviceType
        {
            get { return _deviceType; }
            set { _deviceType = value; }
        }

        private int _levelA0 = -1;
        public int levelA0
        {
            get { return _levelA0; }
            set { _levelA0 = value; }
        }

        private int _levelA1 = -1;
        public int levelA1
        {
            get { return _levelA1; }
            set { _levelA1 = value; }
        }

        private int _levelA2 = -1;
        public int levelA2
        {
            get { return _levelA2; }
            set { _levelA2 = value; }
        }

        private int _levelA3 = -1;
        public int levelA3
        {
            get { return _levelA3; }
            set { _levelA3 = value; }
        }

        private int _eulerX = -1;
        public int eulerX
        {
            get { return _eulerX; }
            set { _eulerX = value; }
        }

        private int _eulerY = -1;
        public int eulerY
        {
            get { return _eulerY; }
            set { _eulerY = value; }
        }

        private int _eulerZ = -1;
        public int eulerZ
        {
            get { return _eulerZ; }
            set { _eulerZ = value; }
        }

        private int _calsys = -1;
        public int calsys
        {
            get { return _calsys; }
            set { _calsys = value; }
        }

        private int _calgyro = -1;
        public int calgyro
        {
            get { return _calgyro; }
            set { _calgyro = value; }
        }

        private int _calacc = -1;
        public int calacc
        {
            get { return _calacc; }
            set { _calacc = value; }
        }

        private int _calmag = -1;
        public int calmag
        {
            get { return _calmag; }
            set { _calmag = value; }
        }

        private int _temp = -1;
        public int temp
        {
            get { return _temp; }
            set { _temp = value; }
        }

    }
}

