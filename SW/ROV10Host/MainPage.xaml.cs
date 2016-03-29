using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Devices.Enumeration;
using Windows.Devices.I2c;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.Gaming.Input;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace ROV10Host
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        // Receive notifications about rotation of the UI and apply any necessary rotation to the preview stream
        private readonly DisplayInformation _displayInformation = DisplayInformation.GetForCurrentView();


        private DisplayOrientations _displayOrientation = DisplayOrientations.Portrait;

        // Rotation metadata to apply to the preview stream (MF_MT_VIDEO_ROTATION)
        // Reference: http://msdn.microsoft.com/en-us/library/windows/apps/xaml/hh868174.aspx
        private static readonly Guid RotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

        // Prevent the screen from sleeping while the camera is running
        private readonly DisplayRequest _displayRequest = new DisplayRequest();

        // MediaCapture and its state variables
        private MediaCapture _mediaCapture;
        private bool _isInitialized = false;
        private bool _isPreviewing = true;
        private bool _isFaceDetected = false;
        private IMediaEncodingProperties _previewProperties;

        // Information about the camera device
        private bool _mirroringPreview = false;

        // Sensor Data
        private DeviceData ddata = new DeviceData(); 

        // Storage Location for settings 
        Windows.Storage.ApplicationDataContainer localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

        public MainPage()
        {
            this.InitializeComponent();
            Settings.Visibility = Visibility.Collapsed;

            //setup default DATA
            ddata.deviceType = "ROV";
            ddata.deviceVersion = "1.0";
        }

        #region AppLCM
        private async void Current_Suspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            // Handle global application events only if this page is active
            if (Frame.CurrentSourcePageType == typeof(MainPage))
            {
                var deferral = e.SuspendingOperation.GetDeferral();

                // close Camera Connection
                await CleanupCameraAsync();
                _displayInformation.OrientationChanged -= DisplayInformation_OrientationChanged;

                deferral.Complete();
            }
        }

        private async void Application_Resuming(object sender, object o)
        {
            // Handle global application events only if this page is active
            if (Frame.CurrentSourcePageType == typeof(MainPage))
            {
                // Populate orientation variables with the current state and register for future changes
                _displayOrientation = _displayInformation.CurrentOrientation;
                _displayInformation.OrientationChanged += DisplayInformation_OrientationChanged;

                //initialize the camera 
                await InitializeCameraAsync(null, null);
            }
        }

        protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            // Handling of this event is included for completenes, as it will only fire when navigating between pages and this sample only includes one page
            await CleanupCameraAsync();
            _displayInformation.OrientationChanged -= DisplayInformation_OrientationChanged;
        }

        enum TragetPlatform
        {
            UNKNOWN = 0,
            IOT_MBM = 1,
            IOT_RASP = 2,
            NON_IOT = 3
        }

        private TragetPlatform targetPlatform = TragetPlatform.UNKNOWN; 

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            string x = Windows.System.Profile.AnalyticsInfo.DeviceForm; 

            if (ApiInformation.IsTypePresent("Windows.Devices.I2c.I2cDevice"))
            {
                targetPlatform = TragetPlatform.UNKNOWN;

                string deviceSelector = I2cDevice.GetDeviceSelector("I2C5");
                var i2cDeviceControllers = await DeviceInformation.FindAllAsync(deviceSelector);
                if (i2cDeviceControllers.Count > 0) targetPlatform = TragetPlatform.IOT_MBM;
                else
                {
                    deviceSelector = I2cDevice.GetDeviceSelector("I2C2");
                    i2cDeviceControllers = await DeviceInformation.FindAllAsync(deviceSelector);
                    if (i2cDeviceControllers.Count > 0) targetPlatform = TragetPlatform.IOT_RASP;
                    else
                    {
                        targetPlatform = TragetPlatform.NON_IOT;
                    }
                }
            }
            else
            {
                targetPlatform = TragetPlatform.NON_IOT; 
            }

            // Populate orientation variables with the current state and register for future changes
            _displayOrientation = _displayInformation.CurrentOrientation;
            _displayInformation.OrientationChanged += DisplayInformation_OrientationChanged;

            int thisViewId = ApplicationView.GetForCurrentView().Id;

            //Init additional Sensors; 
            await Task.Delay(100);
            await InitPCA9685();
            await Task.Delay(100);
            await InitBNO055();

            await Task.Delay(100);
            await InitADS1115();

            await InitializeCameraAsync(null, null);
            await StartSocketListener();

            //await StartServos();
        }
        #endregion AppLCM


        #region ADS1115

        I2cDevice ADS1115Connection = null;
        private const byte ADS1015_ADDRESS = 0x48;     // 1001 000 (ADDR = GND)

        /*========================================================================= 
        CONVERSION DELAY (in mS) 
        -----------------------------------------------------------------------*/
        private const byte ADS1015_CONVERSIONDELAY = 1;
        private const byte ADS1115_CONVERSIONDELAY = 8;
        /*=========================================================================*/

        /*========================================================================= 
        POINTER REGISTER 
        -----------------------------------------------------------------------*/
        private const byte ADS1015_REG_POINTER_MASK         = 0x03;
        private const byte ADS1015_REG_POINTER_CONVERT      = 0x00;
        private const byte ADS1015_REG_POINTER_CONFIG       = 0x01;
        private const byte ADS1015_REG_POINTER_LOWTHRESH    = 0x02;
        private const byte ADS1015_REG_POINTER_HITHRESH     = 0x03;
        /*=========================================================================*/

        /*========================================================================= 
        CONFIG REGISTER 
        -----------------------------------------------------------------------*/
        private const UInt16 ADS1015_REG_CONFIG_OS_MASK     = 0x8000;
        private const UInt16 ADS1015_REG_CONFIG_OS_SINGLE   = 0x8000;       // Write: Set to start a single-conversion 
        private const UInt16 ADS1015_REG_CONFIG_OS_BUSY     = 0x0000;       // Read: Bit = 0 when conversion is in progress 
        private const UInt16 ADS1015_REG_CONFIG_OS_NOTBUSY  = 0x8000;       // Read: Bit = 1 when device is not performing a conversion 

        private const UInt16 ADS1015_REG_CONFIG_MUX_MASK        = 0x7000;
        private const UInt16 ADS1015_REG_CONFIG_MUX_DIFF_0_1    = 0x0000;   // Differential P = AIN0, N = AIN1 (default) 
        private const UInt16 ADS1015_REG_CONFIG_MUX_DIFF_0_3    = 0x1000;   // Differential P = AIN0, N = AIN3 
        private const UInt16 ADS1015_REG_CONFIG_MUX_DIFF_1_3    = 0x2000;   // Differential P = AIN1, N = AIN3 
        private const UInt16 ADS1015_REG_CONFIG_MUX_DIFF_2_3    = 0x3000;   // Differential P = AIN2, N = AIN3 
        private const UInt16 ADS1015_REG_CONFIG_MUX_SINGLE_0    = 0x4000;   // Single-ended AIN0 
        private const UInt16 ADS1015_REG_CONFIG_MUX_SINGLE_1    = 0x5000;   // Single-ended AIN1 
        private const UInt16 ADS1015_REG_CONFIG_MUX_SINGLE_2    = 0x6000;   // Single-ended AIN2 
        private const UInt16 ADS1015_REG_CONFIG_MUX_SINGLE_3    = 0x7000;   // Single-ended AIN3 

        private const UInt16 ADS1015_REG_CONFIG_PGA_MASK        = 0x0E00;
        private const UInt16 ADS1015_REG_CONFIG_PGA_6_144V      = 0x0000;   // +/-6.144V range = Gain 2/3 
        private const UInt16 ADS1015_REG_CONFIG_PGA_4_096V      = 0x0200;   // +/-4.096V range = Gain 1 
        private const UInt16 ADS1015_REG_CONFIG_PGA_2_048V      = 0x0400;   // +/-2.048V range = Gain 2 (default) 
        private const UInt16 ADS1015_REG_CONFIG_PGA_1_024V      = 0x0600;   // +/-1.024V range = Gain 4 
        private const UInt16 ADS1015_REG_CONFIG_PGA_0_512V      = 0x0800;   // +/-0.512V range = Gain 8 
        private const UInt16 ADS1015_REG_CONFIG_PGA_0_256V      = 0x0A00;   // +/-0.256V range = Gain 16 

        private const UInt16 ADS1015_REG_CONFIG_MODE_MASK       = 0x0100;
        private const UInt16 ADS1015_REG_CONFIG_MODE_CONTIN     = 0x0000;  // Continuous conversion mode 
        private const UInt16 ADS1015_REG_CONFIG_MODE_SINGLE     = 0x0100;  // Power-down single-shot mode (default) 

        private const UInt16 ADS1015_REG_CONFIG_DR_MASK         = 0x00E0;
        private const UInt16 ADS1015_REG_CONFIG_DR_128SPS       = 0x0000;  // 128 samples per second 
        private const UInt16 ADS1015_REG_CONFIG_DR_250SPS       = 0x0020;  // 250 samples per second 
        private const UInt16 ADS1015_REG_CONFIG_DR_490SPS       = 0x0040;  // 490 samples per second 
        private const UInt16 ADS1015_REG_CONFIG_DR_920SPS       = 0x0060;  // 920 samples per second 
        private const UInt16 ADS1015_REG_CONFIG_DR_1600SPS      = 0x0080;  // 1600 samples per second (default) 
        private const UInt16 ADS1015_REG_CONFIG_DR_2400SPS      = 0x00A0;  // 2400 samples per second 
        private const UInt16 ADS1015_REG_CONFIG_DR_3300SPS      = 0x00C0;  // 3300 samples per second 

        private const UInt16 ADS1015_REG_CONFIG_CMODE_MASK      = 0x0010;
        private const UInt16 ADS1015_REG_CONFIG_CMODE_TRAD      = 0x0000;  // Traditional comparator with hysteresis (default) 
        private const UInt16 ADS1015_REG_CONFIG_CMODE_WINDOW    = 0x0010;  // Window comparator 

        private const UInt16 ADS1015_REG_CONFIG_CPOL_MASK       = 0x0008;
        private const UInt16 ADS1015_REG_CONFIG_CPOL_ACTVLOW    = 0x0000;  // ALERT/RDY pin is low when active (default) 
        private const UInt16 ADS1015_REG_CONFIG_CPOL_ACTVHI     = 0x0008;  // ALERT/RDY pin is high when active 

        private const UInt16 ADS1015_REG_CONFIG_CLAT_MASK       = 0x0004;  // Determines if ALERT/RDY pin latches once asserted 
        private const UInt16 ADS1015_REG_CONFIG_CLAT_NONLAT     = 0x0000;  // Non-latching comparator (default) 
        private const UInt16 ADS1015_REG_CONFIG_CLAT_LATCH      = 0x0004;  // Latching comparator 

        private const UInt16 ADS1015_REG_CONFIG_CQUE_MASK       = 0x0003;
        private const UInt16 ADS1015_REG_CONFIG_CQUE_1CONV      = 0x0000;  // Assert ALERT/RDY after one conversions 
        private const UInt16 ADS1015_REG_CONFIG_CQUE_2CONV      = 0x0001;  // Assert ALERT/RDY after two conversions 
        private const UInt16 ADS1015_REG_CONFIG_CQUE_4CONV      = 0x0002;  // Assert ALERT/RDY after four conversions 
        private const UInt16 ADS1015_REG_CONFIG_CQUE_NONE       = 0x0003;  // Disable the comparator and put ALERT/RDY in high state (default) 
                                                                           
        /*=========================================================================*/

        enum adsGain_t
        {
            GAIN_TWOTHIRDS = ADS1015_REG_CONFIG_PGA_6_144V, 
            GAIN_ONE = ADS1015_REG_CONFIG_PGA_4_096V, 
            GAIN_TWO = ADS1015_REG_CONFIG_PGA_2_048V, 
            GAIN_FOUR = ADS1015_REG_CONFIG_PGA_1_024V, 
            GAIN_EIGHT = ADS1015_REG_CONFIG_PGA_0_512V, 
            GAIN_SIXTEEN = ADS1015_REG_CONFIG_PGA_0_256V
        }

        private byte m_i2cAddress; 
        private byte m_conversionDelay; 
        private byte m_bitShift; 
        private adsGain_t m_gain;

        private DispatcherTimer ADTimer = null; 

        public async Task<bool> InitADS1115()
        {
            try
            {
                var i2cSettings = new I2cConnectionSettings(ADS1015_ADDRESS); // connect to default address; 
                i2cSettings.BusSpeed = I2cBusSpeed.StandardMode;
                string deviceSelector = I2cDevice.GetDeviceSelector("I2C5");
                var i2cDeviceControllers = await DeviceInformation.FindAllAsync(deviceSelector);
                ADS1115Connection = await I2cDevice.FromIdAsync(i2cDeviceControllers[0].Id, i2cSettings);
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine("Exception: {0}", e.Message);
                return false;
            }

            m_i2cAddress = ADS1015_ADDRESS;
            m_conversionDelay = ADS1115_CONVERSIONDELAY;
            m_bitShift = 0;
            m_gain = adsGain_t.GAIN_TWOTHIRDS; /* +/- 6.144V range (limited to VDD +0.3V max!) */

            try {
                await readADC_SingleEnded(0);
            }
            catch
            {
                linea1.Text = "Error AD Converter";
                return false; 
            }

            ADTimer = new DispatcherTimer();
            ADTimer.Interval = new TimeSpan(0, 0, 1);
            ADTimer.Tick += ADTimer_Tick;
            ADTimer.Start(); 
            return true;
        }

        // Setting these values incorrectly may destroy your ADC! 
        //                                                                ADS1015  ADS1115 
        //                                                                -------  ------- 
        // ads.setGain(GAIN_TWOTHIRDS);  // 2/3x gain +/- 6.144V  1 bit = 3mV      0.1875mV (default) 
        // ads.setGain(GAIN_ONE);        // 1x gain   +/- 4.096V  1 bit = 2mV      0.125mV 
        // ads.setGain(GAIN_TWO);        // 2x gain   +/- 2.048V  1 bit = 1mV      0.0625mV 
        // ads.setGain(GAIN_FOUR);       // 4x gain   +/- 1.024V  1 bit = 0.5mV    0.03125mV 
        // ads.setGain(GAIN_EIGHT);      // 8x gain   +/- 0.512V  1 bit = 0.25mV   0.015625mV 
        // ads.setGain(GAIN_SIXTEEN);    // 16x gain  +/- 0.256V  1 bit = 0.125mV  0.0078125mV 

        private async void ADTimer_Tick(object sender, object e)
        {
            if (ADTimer != null)
            {
                ADTimer.Stop();

                Int16 a1 = (Int16)await readADC_SingleEnded(0) ;
                double a1n = a1 * 0.1875;
                ddata.levelA0 = (int)a1n;
                linea1.Text = a1n.ToString("A0: ####.####") + "mV";

                Int16 a2 = (Int16)await readADC_SingleEnded(1);
                double a2n = a2 * 0.1875;
                ddata.levelA1 = (int)a2n;
                linea2.Text = a2n.ToString("A1: ####.####") + "mV";

                Int16 a3 = (Int16)await readADC_SingleEnded(2);
                double a3n = a3 * 0.1875;
                ddata.levelA2 = (int)a3n;
                linea3.Text = a3n.ToString("A2: ####.####") + "mV";

                Int16 a4 = (Int16)await readADC_SingleEnded(3);
                double a4n = a4 * 0.1875;
                ddata.levelA3 = (int)a4n;
                linea4.Text = a4n.ToString("A3: ####.####") + "mV";
                ADTimer.Start();
            }   
        }

        public async Task<UInt16> readADC_SingleEnded(UInt16 channel)
        {
            if (channel > 3)
            {
                return 0;
            }

            // Start with default values 
            UInt16 config = ADS1015_REG_CONFIG_CQUE_NONE |      // Disable the comparator (default val) 
                            ADS1015_REG_CONFIG_CLAT_NONLAT |    // Non-latching (default val) 
                            ADS1015_REG_CONFIG_CPOL_ACTVLOW |   // Alert/Rdy active low   (default val) 
                            ADS1015_REG_CONFIG_CMODE_TRAD |     // Traditional comparator (default val) 
                            ADS1015_REG_CONFIG_DR_1600SPS |     // 1600 samples per second (default) 
                            ADS1015_REG_CONFIG_MODE_SINGLE;     // Single-shot mode (default) 
            // Set PGA/voltage range 
            config |= (UInt16)m_gain;


            await Task.Delay(140);

            // Set single-ended input channel 
            switch (channel)
            {
                    case (0): 
                        config |= ADS1015_REG_CONFIG_MUX_SINGLE_0;
                        break;
                    case (1): 
                        config |= ADS1015_REG_CONFIG_MUX_SINGLE_1;
                        break;
                    case (2): 
                        config |= ADS1015_REG_CONFIG_MUX_SINGLE_2;
                        break;
                    case (3): 
                        config |= ADS1015_REG_CONFIG_MUX_SINGLE_3;
                        break;
            }

            // Set 'start single-conversion' bit 
            config |= ADS1015_REG_CONFIG_OS_SINGLE;


            // Write config register to the ADC 
            ADS1115_writeRegister(ADS1015_REG_POINTER_CONFIG, config);

            await Task.Delay(140);

            return ADS1115_readRegister(ADS1015_REG_POINTER_CONVERT);
        }

        public void ADS1115_writeRegister(byte reg, UInt16 value)
        {
            if (ADS1115Connection == null) return;

            byte[] i2cBuffer = new byte[3];
            i2cBuffer[0] = (byte)(reg);
            i2cBuffer[1] = (byte)(value>>8);
            i2cBuffer[2] = (byte)(value & 0xff);
            ADS1115Connection.Write(i2cBuffer);
        }

        public UInt16 ADS1115_readRegister(byte reg)
        {
            if (ADS1115Connection == null) return 0;
            ADS1115Connection.Write(new byte[] { reg });
            byte[] i2cBuffer = new byte[2];
            ADS1115Connection.Read(i2cBuffer);
            UInt16 r = (UInt16)((i2cBuffer[0] << 8) | (i2cBuffer[0]));
            return r; 
        }

        #endregion

        #region I2CServo

        I2cDevice PCA9685Connection = null;
        private const byte PCA9685_SUBADR1 = 0x2;
        private const byte PCA9685_SUBADR2 = 0x3;
        private const byte PCA9685_SUBADR3 = 0x4;

        private const byte PCA9685_MODE1 = 0x0;
        private const byte PCA9685_PRESCALE = 0xFE;

        private const byte LED0_ON_L = 0x6;
        private const byte LED0_ON_H = 0x7;

        private const byte LED0_OFF_L = 0x8;
        private const byte LED0_OFF_H = 0x9;

        private const byte ALLLED_ON_L = 0xFA;
        private const byte ALLLED_ON_H = 0xFB;
        private const byte ALLLED_OFF_L = 0xFC;
        private const byte ALLLED_OFF_H = 0xFD;

        public async Task<bool> InitPCA9685()
        {
            try
            {
                var i2cSettings = new I2cConnectionSettings(0x40); // connect to default address; 
                i2cSettings.BusSpeed = I2cBusSpeed.StandardMode;
                //string deviceSelector = I2cDevice.GetDeviceSelector("I2C5");
                string deviceSelector = I2cDevice.GetDeviceSelector("I2C0");
                var i2cDeviceControllers = await DeviceInformation.FindAllAsync(deviceSelector);
                PCA9685Connection = await I2cDevice.FromIdAsync(i2cDeviceControllers[0].Id, i2cSettings);
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine("Exception: {0}", e.Message);
                return false;
            }



            // reset chip; 
            writeByte(PCA9685_MODE1, 0);

            float freq = 60;

            freq *= (float)0.9;  // Correct for overshoot in the frequency setting (see issue #11). 
            float prescaleval = 25000000;
            prescaleval /= 4096;
            prescaleval /= freq;
            prescaleval -= 1;

            byte prescale = (byte)Math.Floor(prescaleval + 0.5);

            byte oldmode = readByte(PCA9685_MODE1);
            byte newmode = (byte)(((byte)oldmode & (byte)0x7F) | (byte)0x10); // sleep 
            writeByte(PCA9685_MODE1, newmode);         // go to sleep 
            byte xoldmode = readByte(PCA9685_MODE1);
            writeByte(PCA9685_PRESCALE, prescale);     // set the prescaler 
            writeByte(PCA9685_MODE1, oldmode);
            await Task.Delay(100);
            writeByte(PCA9685_MODE1, (byte)((byte)oldmode | (byte)0xa1));  //  This sets the MODE1 register to turn on auto increment. 
                                                                           // This is why the beginTransmission below was not working 

            if (localSettings.Values["S1Offset"] == null)
            {
                localSettings.Values["S1Offset"] = (double)0;
            }
            else
            {
                S1_offset = (double)localSettings.Values["S1Offset"]; 
            }
            S1ValueText.Text = S1_offset.ToString();

            if (localSettings.Values["S2Offset"] == null)
            {
                localSettings.Values["S2Offset"] = (double)0;
            }
            else
            {
                S2_offset = (double)localSettings.Values["S2Offset"];
            }
            S2ValueText.Text = S2_offset.ToString();

            if (localSettings.Values["S3Offset"] == null)
            {
                localSettings.Values["S3Offset"] = (double)0;
            }
            else
            {
                S3_offset = (double)localSettings.Values["S3Offset"];
            }
            S3ValueText.Text = S3_offset.ToString();

            if (localSettings.Values["S4Offset"] == null)
            {
                localSettings.Values["S4Offset"] = (double)0;
            }
            else
            {
                S4_offset = (double)localSettings.Values["S4Offset"];
            }
            S4ValueText.Text = S4_offset.ToString();


            setServo(15, 0, S1_offset);
            setServo(14, 0, S2_offset);
            setServo(13, 0, S3_offset);
            setServo(12, 0, S4_offset);

            //setServo(0, -50);
            //await Task.Delay(300);
            //setServo(0, 0);
            //await Task.Delay(300);
            //setServo(0, 50);
            //setServo(1, -50);
            //await Task.Delay(300);
            //setServo(1, 0);
            //await Task.Delay(300);
            //setServo(1, 50);


            if (ServoMaxTimer != null) ServoMaxTimer.Stop();
            ServoMaxTimer.Interval = new TimeSpan(0, 0, 0, 0, 20);
            ServoMaxTimer.Tick += ServoMaxTimer_Tick;

            ServoMaxTimer.Start(); 

            return true;
        }

        DispatcherTimer ServoMaxTimer = new DispatcherTimer();

        void setServo(byte num, double x, double offset )
        {

            const double min_input = -100;
            const double max_input = 100;

            double range_input = max_input - min_input;

            const int min_output = 180;
            const int max_output = 500;

            int range_output = max_output - min_output;

            int y = ((int)(Math.Floor((x - min_input) / range_input * range_output))) + min_output + (int)offset;
            //Console.WriteLine("y is {0}", y);

            UInt16 iy = Convert.ToUInt16(y);
            setPWM(num, 0, iy);
        }

        double S1_old = 0;
        double S1_zerocounter = 0; 
        double S2_old = 0;
        double S2_zerocounter = 0;
        double S3_old = 0;
        double S3_zerocounter = 0;
        double S4_old = 0;
        double S4_zerocounter = 0;

        double S1_offset = 0;
        double S2_offset = 0;
        double S3_offset = 0;
        double S4_offset = 0;


        private async void ServoMaxTimer_Tick(object sender, object e)
        {
            ServoMaxTimer.Stop();
            await Task.Delay(1);
            if (S1_zerocounter > 0) {
                S1_zerocounter--;
                setServo(15, 0, S1_offset);
            }
            else {
                if (((S1_old > 0) && (S1 < 0))|| ((S1_old < 0) && (S1 > 0))) {
                    S1_zerocounter = 2;
                    S1_old = 0; 
                    setServo(15, 0, S1_offset);
                }
                else {
                    setServo(15, S1, S1_offset);
                    S1_old = S1;
                }
            }

            await Task.Delay(1);
            if (S2_zerocounter > 0)
            {
                S2_zerocounter--;
                setServo(14, 0, S2_offset);
            }
            else {
                if (((S2_old > 0) && (S2 < 0)) || ((S2_old < 0) && (S2 > 0)))
                {
                    S2_zerocounter = 2;
                    S2_old = 0;
                    setServo(14, 0, S2_offset);
                }
                else {
                    setServo(14, S2, S2_offset);
                    S2_old = S2;
                }
            }


            await Task.Delay(1);
            if (S3_zerocounter > 0)
            {
                S3_zerocounter--;
                setServo(13, 0, S3_offset);
            }
            else {
                if (((S3_old > 0) && (S3 < 0)) || ((S3_old < 0) && (S3 > 0)))
                {
                    S3_zerocounter = 2;
                    S3_old = 0;
                    setServo(13, 0, S3_offset);
                }
                else {
                    setServo(13, S3, S3_offset);
                    S3_old = S3;
                }
            }

            await Task.Delay(1);
            if (S4_zerocounter > 0)
            {
                S4_zerocounter--;
                setServo(12, 0, S4_offset);
            }
            else {
                if (((S4_old > 0) && (S4 < 0)) || ((S4_old < 0) && (S4 > 0)))
                {
                    S4_zerocounter = 2;
                    S4_old = 0;
                    setServo(12, 0, S4_offset);
                }
                else {
                    setServo(12, S4, S4_offset);
                    S4_old = S4;
                }
            }

            ServoMaxTimer.Start(); 
        }

        public void setPWM(byte num, UInt16 on, UInt16 off)
        {
            if (PCA9685Connection == null) return;
            byte[] i2cBuffer = new byte[5];
            i2cBuffer[0] = (byte)(LED0_ON_L + 4 * num);
            i2cBuffer[1] = (byte)on;
            i2cBuffer[2] = (byte)(on >> 8);
            i2cBuffer[3] = (byte)off;
            i2cBuffer[4] = (byte)(off >> 8);
            PCA9685Connection.Write(i2cBuffer);
        }

        // Sets pin without having to deal with on/off tick placement and properly handles 
        // a zero value as completely off.  Optional invert parameter supports inverting 
        // the pulse for sinking to ground.  Val should be a value from 0 to 4095 inclusive. 
        void setPin(byte num, UInt16 val, bool invert)
        {
            // Clamp value between 0 and 4095 inclusive. 
            //val = Math.min(val, 4095);

            if (val > 4095) val = 4095;

            if (invert)
            {
                if (val == 0)
                {
                    // Special value for signal fully on. 
                    setPWM(num, 4096, 0);
                }
                else if (val == 4095)
                {
                    // Special value for signal fully off. 
                    setPWM(num, 0, 4096);
                }
                else
                {
                    setPWM(num, 0, (UInt16)(4095 - val));
                }
            }
            else
            {
                if (val == 4095)
                {
                    // Special value for signal fully on. 
                    setPWM(num, 4096, 0);
                }
                else if (val == 0)
                {
                    // Special value for signal fully off. 
                    setPWM(num, 0, 4096);
                }
                else
                {
                    setPWM(num, 0, val);
                }
            }
        }

        void setServoPulse(byte n, double pulse)
        {
            double pulselength;
            pulselength = 1000000;  // 1,000,000 us per second 
            pulselength /= 60;      // 60 Hz 
            pulselength /= 4096;    // 12 bits of resolution 
            pulse *= 1000;
            pulse /= pulselength;
            setPWM(n, 0, (UInt16)pulse);
        }

        #endregion

        #region i2chelper

        public byte readByte(byte adr)
        {
            byte[] i2cBuffer = new byte[1];
            //MSB first return value
            PCA9685Connection.Write(new byte[] { adr });
            PCA9685Connection.Read(i2cBuffer);
            return i2cBuffer[0];
        }

        public void writeByte(byte adr, byte data)
        {
            byte[] i2cBuffer = new byte[2];
            i2cBuffer[0] = adr;
            i2cBuffer[1] = data;
            PCA9685Connection.Write(i2cBuffer);
        }

        public byte BNO055readByte(byte adr)
        {
            byte[] i2cBuffer = new byte[20];
            //MSB first return value
            BNO055Connection.Write(new byte[] { adr });
            BNO055Connection.Read(i2cBuffer);
            return i2cBuffer[0];
        }

        public void BNO055Byte(byte adr, byte data)
        {
            byte[] i2cBuffer = new byte[2];
            i2cBuffer[0] = adr;
            i2cBuffer[1] = data;
            BNO055Connection.Write(i2cBuffer);
        }

        #endregion

        #region BNO055
        I2cDevice BNO055Connection = null;

        enum BNO055base
        {
            BNO055_ADDRESS_A = 0x28,
            BNO055_ADDRESS_B = 0x29,
            BNO055_ID = 0xA0
        }
        enum BNO055
        {

            BNO055_PAGE_ID_ADDR = 0X07,

            /* PAGE0 REGISTER DEFINITION START*/
            BNO055_CHIP_ID_ADDR = 0x00,
            BNO055_ACCEL_REV_ID_ADDR = 0x01,
            BNO055_MAG_REV_ID_ADDR = 0x02,
            BNO055_GYRO_REV_ID_ADDR = 0x03,
            BNO055_SW_REV_ID_LSB_ADDR = 0x04,
            BNO055_SW_REV_ID_MSB_ADDR = 0x05,
            BNO055_BL_REV_ID_ADDR = 0X06,

            /* Accel data register */
            BNO055_ACCEL_DATA_X_LSB_ADDR = 0X08,
            BNO055_ACCEL_DATA_X_MSB_ADDR = 0X09,
            BNO055_ACCEL_DATA_Y_LSB_ADDR = 0X0A,
            BNO055_ACCEL_DATA_Y_MSB_ADDR = 0X0B,
            BNO055_ACCEL_DATA_Z_LSB_ADDR = 0X0C,
            BNO055_ACCEL_DATA_Z_MSB_ADDR = 0X0D,

            /* Mag data register */
            BNO055_MAG_DATA_X_LSB_ADDR = 0X0E,
            BNO055_MAG_DATA_X_MSB_ADDR = 0X0F,
            BNO055_MAG_DATA_Y_LSB_ADDR = 0X10,
            BNO055_MAG_DATA_Y_MSB_ADDR = 0X11,
            BNO055_MAG_DATA_Z_LSB_ADDR = 0X12,
            BNO055_MAG_DATA_Z_MSB_ADDR = 0X13,

            /* Gyro data registers */
            BNO055_GYRO_DATA_X_LSB_ADDR = 0X14,
            BNO055_GYRO_DATA_X_MSB_ADDR = 0X15,
            BNO055_GYRO_DATA_Y_LSB_ADDR = 0X16,
            BNO055_GYRO_DATA_Y_MSB_ADDR = 0X17,
            BNO055_GYRO_DATA_Z_LSB_ADDR = 0X18,
            BNO055_GYRO_DATA_Z_MSB_ADDR = 0X19,

            /* Euler data registers */
            BNO055_EULER_H_LSB_ADDR = 0X1A,
            BNO055_EULER_H_MSB_ADDR = 0X1B,
            BNO055_EULER_R_LSB_ADDR = 0X1C,
            BNO055_EULER_R_MSB_ADDR = 0X1D,
            BNO055_EULER_P_LSB_ADDR = 0X1E,
            BNO055_EULER_P_MSB_ADDR = 0X1F,

            /* Quaternion data registers */
            BNO055_QUATERNION_DATA_W_LSB_ADDR = 0X20,
            BNO055_QUATERNION_DATA_W_MSB_ADDR = 0X21,
            BNO055_QUATERNION_DATA_X_LSB_ADDR = 0X22,
            BNO055_QUATERNION_DATA_X_MSB_ADDR = 0X23,
            BNO055_QUATERNION_DATA_Y_LSB_ADDR = 0X24,
            BNO055_QUATERNION_DATA_Y_MSB_ADDR = 0X25,
            BNO055_QUATERNION_DATA_Z_LSB_ADDR = 0X26,
            BNO055_QUATERNION_DATA_Z_MSB_ADDR = 0X27,

            /* Linear acceleration data registers */
            BNO055_LINEAR_ACCEL_DATA_X_LSB_ADDR = 0X28,
            BNO055_LINEAR_ACCEL_DATA_X_MSB_ADDR = 0X29,
            BNO055_LINEAR_ACCEL_DATA_Y_LSB_ADDR = 0X2A,
            BNO055_LINEAR_ACCEL_DATA_Y_MSB_ADDR = 0X2B,
            BNO055_LINEAR_ACCEL_DATA_Z_LSB_ADDR = 0X2C,
            BNO055_LINEAR_ACCEL_DATA_Z_MSB_ADDR = 0X2D,

            /* Gravity data registers */
            BNO055_GRAVITY_DATA_X_LSB_ADDR = 0X2E,
            BNO055_GRAVITY_DATA_X_MSB_ADDR = 0X2F,
            BNO055_GRAVITY_DATA_Y_LSB_ADDR = 0X30,
            BNO055_GRAVITY_DATA_Y_MSB_ADDR = 0X31,
            BNO055_GRAVITY_DATA_Z_LSB_ADDR = 0X32,
            BNO055_GRAVITY_DATA_Z_MSB_ADDR = 0X33,

            /* Temperature data register */
            BNO055_TEMP_ADDR = 0X34,

            /* Status registers */
            BNO055_CALIB_STAT_ADDR = 0X35,
            BNO055_SELFTEST_RESULT_ADDR = 0X36,
            BNO055_INTR_STAT_ADDR = 0X37,

            BNO055_SYS_CLK_STAT_ADDR = 0X38,
            BNO055_SYS_STAT_ADDR = 0X39,
            BNO055_SYS_ERR_ADDR = 0X3A,

            /* Unit selection register */
            BNO055_UNIT_SEL_ADDR = 0X3B,
            BNO055_DATA_SELECT_ADDR = 0X3C,

            /* Mode registers */
            BNO055_OPR_MODE_ADDR = 0X3D,
            BNO055_PWR_MODE_ADDR = 0X3E,

            BNO055_SYS_TRIGGER_ADDR = 0X3F,
            BNO055_TEMP_SOURCE_ADDR = 0X40,

            /* Axis remap registers */
            BNO055_AXIS_MAP_CONFIG_ADDR = 0X41,
            BNO055_AXIS_MAP_SIGN_ADDR = 0X42,

            /* SIC registers */
            BNO055_SIC_MATRIX_0_LSB_ADDR = 0X43,
            BNO055_SIC_MATRIX_0_MSB_ADDR = 0X44,
            BNO055_SIC_MATRIX_1_LSB_ADDR = 0X45,
            BNO055_SIC_MATRIX_1_MSB_ADDR = 0X46,
            BNO055_SIC_MATRIX_2_LSB_ADDR = 0X47,
            BNO055_SIC_MATRIX_2_MSB_ADDR = 0X48,
            BNO055_SIC_MATRIX_3_LSB_ADDR = 0X49,
            BNO055_SIC_MATRIX_3_MSB_ADDR = 0X4A,
            BNO055_SIC_MATRIX_4_LSB_ADDR = 0X4B,
            BNO055_SIC_MATRIX_4_MSB_ADDR = 0X4C,
            BNO055_SIC_MATRIX_5_LSB_ADDR = 0X4D,
            BNO055_SIC_MATRIX_5_MSB_ADDR = 0X4E,
            BNO055_SIC_MATRIX_6_LSB_ADDR = 0X4F,
            BNO055_SIC_MATRIX_6_MSB_ADDR = 0X50,
            BNO055_SIC_MATRIX_7_LSB_ADDR = 0X51,
            BNO055_SIC_MATRIX_7_MSB_ADDR = 0X52,
            BNO055_SIC_MATRIX_8_LSB_ADDR = 0X53,
            BNO055_SIC_MATRIX_8_MSB_ADDR = 0X54,

            /* Accelerometer Offset registers */
            ACCEL_OFFSET_X_LSB_ADDR = 0X55,
            ACCEL_OFFSET_X_MSB_ADDR = 0X56,
            ACCEL_OFFSET_Y_LSB_ADDR = 0X57,
            ACCEL_OFFSET_Y_MSB_ADDR = 0X58,
            ACCEL_OFFSET_Z_LSB_ADDR = 0X59,
            ACCEL_OFFSET_Z_MSB_ADDR = 0X5A,

            /* Magnetometer Offset registers */
            MAG_OFFSET_X_LSB_ADDR = 0X5B,
            MAG_OFFSET_X_MSB_ADDR = 0X5C,
            MAG_OFFSET_Y_LSB_ADDR = 0X5D,
            MAG_OFFSET_Y_MSB_ADDR = 0X5E,
            MAG_OFFSET_Z_LSB_ADDR = 0X5F,
            MAG_OFFSET_Z_MSB_ADDR = 0X60,

            /* Gyroscope Offset register s*/
            GYRO_OFFSET_X_LSB_ADDR = 0X61,
            GYRO_OFFSET_X_MSB_ADDR = 0X62,
            GYRO_OFFSET_Y_LSB_ADDR = 0X63,
            GYRO_OFFSET_Y_MSB_ADDR = 0X64,
            GYRO_OFFSET_Z_LSB_ADDR = 0X65,
            GYRO_OFFSET_Z_MSB_ADDR = 0X66,

            /* Radius registers */
            ACCEL_RADIUS_LSB_ADDR = 0X67,
            ACCEL_RADIUS_MSB_ADDR = 0X68,
            MAG_RADIUS_LSB_ADDR = 0X69,
            MAG_RADIUS_MSB_ADDR = 0X6A,

            POWER_MODE_NORMAL = 0X00,
            POWER_MODE_LOWPOWER = 0X01,
            POWER_MODE_SUSPEND = 0X02,

            /* Operation mode settings*/
            OPERATION_MODE_CONFIG = 0X00,
            OPERATION_MODE_ACCONLY = 0X01,
            OPERATION_MODE_MAGONLY = 0X02,
            OPERATION_MODE_GYRONLY = 0X03,
            OPERATION_MODE_ACCMAG = 0X04,
            OPERATION_MODE_ACCGYRO = 0X05,
            OPERATION_MODE_MAGGYRO = 0X06,
            OPERATION_MODE_AMG = 0X07,
            OPERATION_MODE_IMUPLUS = 0X08,
            OPERATION_MODE_COMPASS = 0X09,
            OPERATION_MODE_M4G = 0X0A,
            OPERATION_MODE_NDOF_FMC_OFF = 0X0B,
            OPERATION_MODE_NDOF = 0X0C
        };
        enum VectorDef
        {
            VECTOR_ACCELEROMETER = BNO055.BNO055_ACCEL_DATA_X_LSB_ADDR,
            VECTOR_MAGNETOMETER = BNO055.BNO055_MAG_DATA_X_LSB_ADDR,
            VECTOR_GYROSCOPE = BNO055.BNO055_GYRO_DATA_X_LSB_ADDR,
            VECTOR_EULER = BNO055.BNO055_EULER_H_LSB_ADDR,
            VECTOR_LINEARACCEL = BNO055.BNO055_LINEAR_ACCEL_DATA_X_LSB_ADDR,
            VECTOR_GRAVITY = BNO055.BNO055_GRAVITY_DATA_X_LSB_ADDR
        };

        public async Task<bool> InitBNO055()
        {
            try
            {
                var i2cSettings = new I2cConnectionSettings((byte)0x28); // connect to default address; 
                i2cSettings.BusSpeed = I2cBusSpeed.StandardMode;
                string deviceSelector = I2cDevice.GetDeviceSelector("I2C5");
                var i2cDeviceControllers = await DeviceInformation.FindAllAsync(deviceSelector);
                BNO055Connection = await I2cDevice.FromIdAsync(i2cDeviceControllers[0].Id, i2cSettings);
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine("Exception: {0}", e.Message);
                return false;
            }

            byte id = BNO055readByte((byte)BNO055.BNO055_CHIP_ID_ADDR);
            if (id != (byte)BNO055base.BNO055_ID)
            {
                await Task.Delay(1000); // hold on for boot
                id = BNO055readByte((byte)BNO055.BNO055_CHIP_ID_ADDR);
                if (id != (byte)BNO055base.BNO055_ID)
                {
                    return false;  // still not? ok bail
                }
            }

            /* Switch to config mode (just in case since this is the default) */
            await setMode((byte)BNO055.OPERATION_MODE_CONFIG);

            /* Reset */
            BNO055Byte((byte)BNO055.BNO055_SYS_TRIGGER_ADDR, 0x20);
            await Task.Delay(1000);
            while (BNO055readByte((byte)BNO055.BNO055_CHIP_ID_ADDR) != (byte)BNO055base.BNO055_ID)
            {
                await Task.Delay(40);
            }
            await Task.Delay(50);

            /* Set to normal power mode */
            BNO055Byte((byte)BNO055.BNO055_PWR_MODE_ADDR, (byte)BNO055.POWER_MODE_NORMAL);
            await Task.Delay(10);

            BNO055Byte((byte)BNO055.BNO055_PAGE_ID_ADDR, 0);

            /* Set the output units */
            /*
            uint8_t unitsel = (0 << 7) | // Orientation = Android
                              (0 << 4) | // Temperature = Celsius
                              (0 << 2) | // Euler = Degrees
                              (1 << 1) | // Gyro = Rads
                              (0 << 0);  // Accelerometer = m/s^2
            write8(BNO055_UNIT_SEL_ADDR, unitsel);
            */
            BNO055Byte((byte)BNO055.BNO055_SYS_TRIGGER_ADDR, 0x0);
            await Task.Delay(10);

            /* Set the requested operating mode (see section 3.3) */
            await setMode((byte)BNO055.OPERATION_MODE_NDOF);
            await Task.Delay(10);

            int temp = getTemp();
            getCalibration();
            BNO055Vector accl = getVector((byte)VectorDef.VECTOR_ACCELEROMETER);
            BNO055Vector euler = getVector((byte)VectorDef.VECTOR_EULER);
            BNO055Vector gravity = getVector((byte)VectorDef.VECTOR_GRAVITY);
            BNO055Vector gyro = getVector((byte)VectorDef.VECTOR_GYROSCOPE);
            BNO055Vector linaccl = getVector((byte)VectorDef.VECTOR_LINEARACCEL);
            BNO055Vector magnetometer = getVector((byte)VectorDef.VECTOR_MAGNETOMETER);

            IMUTimer = new DispatcherTimer();
            IMUTimer.Interval = new TimeSpan(0, 0, 0, 0, 10);
            IMUTimer.Tick += IMUTimer_Tick;
            IMUTimer.Start();


            return true;
        }

        private async void IMUTimer_Tick(object sender, object e)
        {
            IMUTimer.Stop(); 
            int temp = getTemp();
            getCalibration();
            BNO055Vector accl = getVector((byte)VectorDef.VECTOR_ACCELEROMETER);
            BNO055Vector euler = getVector((byte)VectorDef.VECTOR_EULER);
            BNO055Vector gravity = getVector((byte)VectorDef.VECTOR_GRAVITY);
            BNO055Vector gyrometer = getVector((byte)VectorDef.VECTOR_GYROSCOPE);
            BNO055Vector linaccl = getVector((byte)VectorDef.VECTOR_LINEARACCEL);
            BNO055Vector magnetometer = getVector((byte)VectorDef.VECTOR_MAGNETOMETER);

            ddata.temp = temp;
            ddata.eulerX = (int)euler.X;
            ddata.eulerY = (int)euler.Y;
            ddata.eulerZ = (int)euler.Z;
            ddata.calsys = (int)sys;
            ddata.calacc = (int)accel;
            ddata.calgyro = (int)gyro;
            ddata.calmag = (int)mag;  

            line1.Text = "CALLIBRATION: sys:" + sys.ToString() + " gyro:" + gyro.ToString() + " accel:" + accel.ToString() + " mag:" + mag.ToString();
            line2.Text = "TEMP:" + temp.ToString();
            line3.Text = "ACCELEROMETER: x:" + accl.X.ToString("000.000") + " y:" + accl.Y.ToString("000.000") + " z:" + accl.Z.ToString("000.000");
            line4.Text = "EULER: x:" + euler.X.ToString("000.000") + " y:" + euler.Y.ToString("000.000") + " z:" + euler.Z.ToString("000.000");
            line5.Text = "GRAVITY: x:" + gravity.X.ToString("000.000") + " y:" + gravity.Y.ToString("000.000") + " z:" + gravity.Z.ToString("000.000");
            line6.Text = "GYRO: x:" + gyrometer.X.ToString("000.000") + " y:" + gyrometer.Y.ToString("000.000") + " z:" + gyrometer.Z.ToString("000.000");
            line7.Text = "LINEAR: x:" + linaccl.X.ToString("000.000") + " y:" + linaccl.Y.ToString("000.000") + " z:" + linaccl.Z.ToString("000.000");
            line8.Text = "MAGNETOMETER: x:" + magnetometer.X.ToString("000.000") + " y:" + magnetometer.Y.ToString("000.000") + " z:" + magnetometer.Z.ToString("000.000");

            await Task.Delay(1);
            setServo(0, S1, S1_offset);
            await Task.Delay(1);
            setServo(1, S2, S2_offset);
            await Task.Delay(1);
            setServo(2, S3, S3_offset);
            await Task.Delay(1);
            setServo(3, S4, S4_offset);
            IMUTimer.Start();

        }

        DispatcherTimer IMUTimer = null;

        UInt16 sys = 0;
        UInt16 gyro = 0;
        UInt16 accel = 0;
        UInt16 mag = 0;

        public void getCalibration()
        {
            byte calData = BNO055readByte((byte)BNO055.BNO055_CALIB_STAT_ADDR);
            sys = (UInt16)((calData >> 6) & 0x03);
            gyro = (UInt16)((calData >> 4) & 0x03);
            accel = (UInt16)((calData >> 2) & 0x03);
            mag = (UInt16)(calData & 0x03);
        }

        private async Task setMode(byte mode)
        {
            BNO055Byte((byte)BNO055.BNO055_OPR_MODE_ADDR, mode);
            await Task.Delay(30);
        }

        public int getTemp()
        {
            int temp = (int)BNO055readByte((byte)BNO055.BNO055_TEMP_ADDR);
            return temp;
        }

        public class BNO055Vector
        {
            public double X;
            public double Y;
            public double Z;
        }

        public BNO055Vector getVector(byte type)
        {
            BNO055Vector xyz = new BNO055Vector();

            byte[] i2cBuffer = new byte[6];
            BNO055Connection.Write(new byte[] { (byte)type });
            BNO055Connection.Read(i2cBuffer);

            Int16 x = (Int16)(i2cBuffer[0] | (i2cBuffer[1] << 8));
            Int16 y = (Int16)(i2cBuffer[2] | (i2cBuffer[3] << 8));
            Int16 z = (Int16)(i2cBuffer[4] | (i2cBuffer[5] << 8));

            /* Convert the value to an appropriate range (section 3.6.4) */
            /* and assign the value to the Vector type */

            switch (type)
            {
                case (byte)VectorDef.VECTOR_MAGNETOMETER:
                    /* 1uT = 16 LSB */
                    xyz.X = ((double)x) / 16.0;
                    xyz.Y = ((double)y) / 16.0;
                    xyz.Z = ((double)z) / 16.0;
                    break;
                case (byte)VectorDef.VECTOR_GYROSCOPE:
                    /* 1rps = 900 LSB */
                    xyz.X = ((double)x) / 900.0;
                    xyz.Y = ((double)y) / 900.0;
                    xyz.Z = ((double)z) / 900.0;
                    break;
                case (byte)VectorDef.VECTOR_EULER:
                    /* 1 degree = 16 LSB */
                    xyz.X = ((double)x) / 16.0;
                    xyz.Y = ((double)y) / 16.0;
                    xyz.Z = ((double)z) / 16.0;
                    break;
                case (byte)VectorDef.VECTOR_ACCELEROMETER:
                case (byte)VectorDef.VECTOR_LINEARACCEL:
                case (byte)VectorDef.VECTOR_GRAVITY:
                    /* 1m/s^2 = 100 LSB */
                    xyz.X = ((double)x) / 100.0;
                    xyz.Y = ((double)y) / 100.0;
                    xyz.Z = ((double)z) / 100.0;
                    break;
            }
            return xyz;
        }

        #endregion

        #region Camera

        private ObservableCollection<string> CameraDeviceModeList = new ObservableCollection<string>();
        private ObservableCollection<VideoEncodingProperties> CameraDeviceModeInformation = new ObservableCollection<VideoEncodingProperties>();

        /// <summary>
        /// Initializes the MediaCapture, registers events, gets camera device information for mirroring and rotating, and starts preview
        /// </summary>
        /// <returns></returns>
        private async Task InitializeCameraAsync(DeviceInformation cameraDevice, VideoEncodingProperties cameraMode)
        {
            Debug.WriteLine("InitializeCameraAsync");

            if (_mediaCapture == null)
            {
                if (cameraDevice == null)
                {
                    // Attempt to get the back camera if one is available, but use any camera device if not
                    cameraDevice = await FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel.Back);

                    if (cameraDevice == null)
                    {
                        Debug.WriteLine("No camera device found!");
                        return;
                    }
                }

                // Create MediaCapture and its settings
                _mediaCapture = new MediaCapture();

                // Register for a notification when something goes wrong
                _mediaCapture.Failed += MediaCapture_Failed;

                var settings = new MediaCaptureInitializationSettings { VideoDeviceId = cameraDevice.Id };

                // store ID of final Cam Device
                localSettings.Values["CameraDeviceID"] = cameraDevice.Id.ToUpper();

                // Initialize MediaCapture
                try
                {

                    await _mediaCapture.InitializeAsync(settings);
                    var media = _mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview);

                    CameraDeviceModeList.Clear();
                    CameraDeviceModeInformation.Clear();
                    foreach (VideoEncodingProperties i in media)
                    {
                        string s = i.Height.ToString() + "x" + i.Width.ToString() + " (" + i.Type + "/" + i.Subtype + ")";
                        CameraDeviceModeList.Add(s);
                        CameraDeviceModeInformation.Add(i);
                        CameraModeList.ItemsSource = CameraDeviceModeList;
                    }

                    if (cameraMode == null)
                    {
                        string cameraDeviceModeIndex = (string)localSettings.Values["CameraModeIndex"];
                        try
                        {
                            if (cameraDeviceModeIndex != null)
                            {
                                cameraMode = CameraDeviceModeInformation[(int)Convert.ToUInt32(cameraDeviceModeIndex)];
                            }
                            else
                            {
                                cameraMode = null;
                            }
                        }
                        catch
                        {
                            cameraMode = null;
                        }
                    }

                    //await Task.Delay(100);
                    if (media.Count >= 1)
                    {
                        if (cameraMode == null)
                        {
                            VideoEncodingProperties hires = (VideoEncodingProperties)media.OrderByDescending(item => ((VideoEncodingProperties)item).Width).First();
                            await _mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, hires);
                            SelectDeviceModeinList((VideoEncodingProperties)hires, CameraDeviceModeInformation);
                        }
                        else
                        {
                            await _mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, cameraMode);
                            SelectDeviceModeinList((VideoEncodingProperties)cameraMode, CameraDeviceModeInformation);
                        }
                    }
                    //await Task.Delay(100);
                    _isInitialized = true;
                }

                catch (UnauthorizedAccessException)
                {
                    Debug.WriteLine("The app was denied access to the camera");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Exception when initializing MediaCapture with {0}: {1}", cameraDevice.Id, ex.ToString());
                }

                // If initialization succeeded, start the preview
                if (_isInitialized)
                {
                    // Figure out where the camera is located
                    if (cameraDevice.EnclosureLocation == null || cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Unknown)
                    {
                        // No information on the location of the camera, assume it's an external camera, not integrated on the device
                        //_externalCamera = true;
                    }
                    else
                    {
                        // Camera is fixed on the device
                        //_externalCamera = false;

                        // Only mirror the preview if the camera is on the front panel
                        _mirroringPreview = (cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front);
                    }

                    await StartPreviewAsync();

                    // Clear any rectangles that may have been left over from a previous instance of the effect
                    FacesCanvas.Children.Clear();

                    // start Face detection if enabled; 
                    //await CreateFaceDetectionEffectAsync();
                }
            }
        }

        /// <summary>
        /// Cleans up the camera resources (after stopping the preview if necessary) and unregisters from MediaCapture events
        /// </summary>
        /// <returns></returns>
        private async Task CleanupCameraAsync()
        {
            if (_isInitialized)
            {
                if (_isPreviewing)
                {
                    // The call to stop the preview is included here for completeness, but can be
                    // safely removed if a call to MediaCapture.Dispose() is being made later,
                    // as the preview will be automatically stopped at that point
                    await StopPreviewAsync();
                }

                _isInitialized = false;
            }

            if (MainTimer != null)
            {
                MainTimer.Stop();
                MainTimer = null;
            }

            //if (_faceDetectionEffect != null)
            //{
            //    await CleanUpFaceDetectionEffectAsync();
            //}

            if (_mediaCapture != null)
            {
                _mediaCapture.Failed -= MediaCapture_Failed;
                _mediaCapture.Dispose();
                _mediaCapture = null;
            }
        }

        /// <summary>
        /// This event will fire when the page is rotated
        /// </summary>
        /// <param name="sender">The event source.</param>
        /// <param name="args">The event data.</param>
        private async void DisplayInformation_OrientationChanged(DisplayInformation sender, object args)
        {
            _displayOrientation = sender.CurrentOrientation;

            if (_isPreviewing)
            {
                await SetPreviewRotationAsync();
            }
        }

        private async void MediaCapture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
        {
            Debug.WriteLine("MediaCapture_Failed: (0x{0:X}) {1}", errorEventArgs.Code, errorEventArgs.Message);

            try
            {
                await CleanupCameraAsync();
            }
            catch (Exception ex)
            {
            }

            //await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => GetPreviewFrameButton.IsEnabled = _isPreviewing);
        }

        /// <summary>
        /// Starts the preview and adjusts it for for rotation and mirroring after making a request to keep the screen on and unlocks the UI
        /// </summary>
        /// <returns></returns>
        private async Task StartPreviewAsync()
        {
            Debug.WriteLine("StartPreviewAsync");

            // Prevent the device from sleeping while the preview is running
            _displayRequest.RequestActive();

            // Set the preview source in the UI and mirror it if necessary
            PreviewControl.Source = _mediaCapture;
            PreviewControl.FlowDirection = _mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            // Start the preview
            try
            {
                await _mediaCapture.StartPreviewAsync();

                _isPreviewing = true;
                _previewProperties = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Exception when starting the preview: {0}", ex.ToString());
            }

            // Initialize the preview to the current orientation
            if (_isPreviewing)
            {
                await SetPreviewRotationAsync();
            }

            // Enable / disable the button depending on the preview state
            //GetPreviewFrameButton.IsEnabled = _isPreviewing;

            // Clear any rectangles that may have been left over from a previous instance of the effect
            FacesCanvas.Children.Clear();
            //await CreateFaceDetectionEffectAsync();



            //var t1 = Task.Run(async () =>
            //{
            //    encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);
            //    while (true)
            //    {
            //        counter++;
            //        //StatusText2.Text = "Frame: " + counter.ToString();
            //        await GetPreviewFrameAsSoftwareBitmapAsync();
            //        await Task.Delay(10);
            //    }
            //}
            //);

            MainTimer = new DispatcherTimer();
            MainTimer.Interval = new TimeSpan(0, 0, 0, 0, 10);
            MainTimer.Tick += MainTimer_Tick;
            MainTimer.Start();

        }

        private async void CameraList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!blockcamerarestart)
            {
                await CleanupCameraAsync();
                ComboBox s = (ComboBox)sender;
                localSettings.Values["CameraModeIndex"] = null;

                await InitializeCameraAsync(CameraDeviceInformation[s.SelectedIndex], null);
            }
            blockcamerarestart = false;

        }

        private async void CameraModeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBox s = (ComboBox)sender;
            if (s.SelectedIndex == -1) return;
            if (!blockcameramoderestart)
            {
                await CleanupCameraAsync();
                localSettings.Values["CameraModeIndex"] = s.SelectedIndex.ToString();
                await InitializeCameraAsync(CameraDeviceInformation[CameraList.SelectedIndex], CameraDeviceModeInformation[s.SelectedIndex]);
            }
            blockcameramoderestart = false;
        }

        private int counter = 0; 

        private async void MainTimer_Tick(object sender, object e)
        {
            if (MainTimer == null) return;
            MainTimer.Stop();
            try
            {
                counter++;
                StatusText2.Text = "Frame: " + counter.ToString(); 
                await GetPreviewFrameAsSoftwareBitmapAsync();
            }
            catch (Exception exe)
            {
                var x = 1;
            }
            if (MainTimer != null)
            {
                MainTimer.Interval = new TimeSpan(0,0,0,0,1);
                MainTimer.Start();
            }
        }

        DispatcherTimer MainTimer = null;

        private async Task SetPreviewRotationAsync()
        {
            // Only need to update the orientation if the camera is mounted on the device
            //if (_externalCamera) return;

            // Calculate which way and how far to rotate the preview
            int rotationDegrees = ConvertDisplayOrientationToDegrees(_displayOrientation);

            // The rotation direction needs to be inverted if the preview is being mirrored
            if (_mirroringPreview)
            {
                rotationDegrees = (360 - rotationDegrees) % 360;
            }

            // Add rotation metadata to the preview stream to make sure the aspect ratio / dimensions match when rendering and getting preview frames
            IMediaEncodingProperties props = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
            props.Properties.Add(RotationKey, rotationDegrees);
            await _mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);
        }

        /// <summary>
        /// Stops the preview and deactivates a display request, to allow the screen to go into power saving modes, and locks the UI
        /// </summary>
        /// <returns></returns>
        private async Task StopPreviewAsync()
        {
            try
            {
                _isPreviewing = false;
                await _mediaCapture.StopPreviewAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Exception when stopping the preview: {0}", ex.ToString());
            }

            // Use the dispatcher because this method is sometimes called from non-UI threads
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                PreviewControl.Source = null;

                // Allow the device to sleep now that the preview is stopped
                _displayRequest.RequestRelease();

                //GetPreviewFrameButton.IsEnabled = _isPreviewing;
            });
        }

        #endregion

        #region SocketConnection
        private StreamSocketListener listener = null;

        // List containing all available local HostName endpoints
        private List<LocalHostItem> localHostItems = new List<LocalHostItem>();

        private DataReader _dr = null;
        private DataWriter _dw = null;

        private async Task StartSocketListener()
        {
            localHostItems.Clear();
            InfoText.Text = "";
            foreach (HostName localHostInfo in NetworkInformation.GetHostNames())
            {

                if (localHostInfo.IPInformation != null)
                {
                    LocalHostItem adapterItem = new LocalHostItem(localHostInfo);
                    localHostItems.Add(adapterItem);
                    InfoText.Text += adapterItem.LocalHost.DisplayName + "\n";

                }
            }

            if (localHostItems.Count == 0)
            {
                StatusText.Text = "No Network Interface";
                return;
            }

            try
            {
                listener = new StreamSocketListener();
                listener.Control.NoDelay = true;
                listener.Control.KeepAlive = true;
                //listener.Control.OutboundBufferSizeInBytes = 2000000;
                listener.ConnectionReceived += Listener_ConnectionReceived;
                StatusText.Text = "Waiting for connection";
                NetworkAdapter selectedAdapter = localHostItems[0].LocalHost.IPInformation.NetworkAdapter;

                await listener.BindServiceNameAsync("8081"); // BindServiceNameAsync("80", SocketProtectionLevel.PlainSocket, selectedAdapter);

                foreach (LocalHostItem i in localHostItems)
                {
                    ConnectionProfile x = await i.LocalHost.IPInformation.NetworkAdapter.GetConnectedProfileAsync();
                }
            }
            catch (Exception exception)
            {
                // If this is an unknown status it means that the error is fatal and retry will likely fail.
                if (SocketError.GetStatus(exception.HResult) == SocketErrorStatus.Unknown)
                {
                    throw;
                }

                StatusText.Text = "Start listening failed with error: " + exception.Message;
            }
        }

        private double S1 = 0;
        private double S2 = 0;
        private double S3 = 0;
        private double S4 = 0;

        private async void Listener_ConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            if (_dr == null)
            {
                var ignore = Dispatcher.RunAsync(
                    CoreDispatcherPriority.Normal, () =>
                    {
                        StatusText.Text = "connection received....(" + args.Socket.Information.RemoteAddress.CanonicalName + ")";
                    });
            }
            else
            {
                return;
            }

            _dr = new DataReader(args.Socket.InputStream);
            _dw = new DataWriter(args.Socket.OutputStream);

            var t = Task.Run(async () =>
            {
                try
                {
                    int i = 0;
                    while (true)
                    {
                        uint actualStringLength = await _dr.LoadAsync(1);
                        string r = _dr.ReadString(actualStringLength);
                        if (r == "X")
                        {
                            i++;
                            await _dr.LoadAsync(sizeof(uint));
                            uint jsonstringLength = _dr.ReadUInt32();

                            await _dr.LoadAsync(jsonstringLength);
                            string jsonstring = _dr.ReadString(jsonstringLength);


                            var x = Dispatcher.RunAsync(
                            CoreDispatcherPriority.High, () =>
                            {
                                try
                                {
                                    JsonObject j = JsonObject.Parse(jsonstring);
                                    S1 = j.GetNamedNumber("S1");
                                    S2 = j.GetNamedNumber("S2");
                                    S3 = j.GetNamedNumber("S3");
                                    S4 = j.GetNamedNumber("S4");

                                }
                                catch (Exception e)
                                {
                                    var xx = 1;
                                }
                                StatusText1.Text = "Frame: " + i.ToString() + " " + jsonstring;
                            });
                            blocktransmission = false;
                        }
                    }
                }
                catch (Exception exception)
                {
                    _dr = null;
                    _dw = null;
                    listener.Dispose();
                    listener = null;

                    //var ignore = Dispatcher.RunAsync(
                    //    CoreDispatcherPriority.Normal, () =>
                    //    {
                    //        //StatusText1.Text = "Read stream failed with error: " + exception.Message;
                    //    });
                }

            }
            );

        }

        private bool blocktransmission = false;
        private byte[] xb = null;
        private SoftwareBitmap previewFrame;
        private byte[] frameheader = null;
        //private VideoFrame videoFrame;
        //private VideoEncodingProperties previewProperties;
        //private InMemoryRandomAccessStream ms = new InMemoryRandomAccessStream();
        //BitmapEncoder encoder = null; 



        /// <summary>
        /// Gets the current preview frame as a SoftwareBitmap, displays its properties in a TextBlock, and can optionally display the image
        /// in the UI and/or save it to disk as a jpg
        /// </summary>
        /// <returns></returns>
        private async Task GetPreviewFrameAsSoftwareBitmapAsync()
        {
            // Clear any rectangles that may have been left over from a previous instance of the effect
            var previewProperties = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;

            // Create the video frame to request a SoftwareBitmap preview frame
            var videoFrame = new VideoFrame(BitmapPixelFormat.Bgra8, (int)previewProperties.Width, (int)previewProperties.Height);


            JsonObject jsonObject = new JsonObject();
            jsonObject["Version"] = JsonValue.CreateStringValue(ddata.deviceVersion);
            jsonObject["Type"] = JsonValue.CreateStringValue(ddata.deviceType);
            jsonObject["A0"] = JsonValue.CreateNumberValue((double)ddata.levelA0);
            jsonObject["A1"] = JsonValue.CreateNumberValue((double)ddata.levelA1);
            jsonObject["A2"] = JsonValue.CreateNumberValue((double)ddata.levelA2);
            jsonObject["A3"] = JsonValue.CreateNumberValue((double)ddata.levelA3);
            jsonObject["Calacc"] = JsonValue.CreateNumberValue((double)ddata.calacc);
            jsonObject["Calgyro"] = JsonValue.CreateNumberValue((double)ddata.calgyro);
            jsonObject["Calmag"] = JsonValue.CreateNumberValue((double)ddata.calmag);
            jsonObject["Calsys"] = JsonValue.CreateNumberValue((double)ddata.calsys);
            jsonObject["EulerX"] = JsonValue.CreateNumberValue((double)ddata.eulerX);
            jsonObject["EulerY"] = JsonValue.CreateNumberValue((double)ddata.eulerY);
            jsonObject["EulerZ"] = JsonValue.CreateNumberValue((double)ddata.eulerZ);
            jsonObject["Temp"] = JsonValue.CreateNumberValue((double)ddata.temp);


            // Capture the preview frame
            using (var currentFrame = await _mediaCapture.GetPreviewFrameAsync(videoFrame))  
            {
                // Collect the resulting frame
                previewFrame = currentFrame.SoftwareBitmap;

                if (listener == null)
                {
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        StatusText.Text = "Restarting Server Listener";
                    });
                    await StartSocketListener();
                }


                if (_dw != null)
                {
                    //if (!blocktransmission)
                    //{

                        //previewFrame.LockBuffer(BitmapBufferAccessMode.ReadWrite);

                        //await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, async () =>
                        //{
                            try
                            {

                                //WriteableBitmap newBitmap = new WriteableBitmap(previewFrame.PixelWidth, previewFrame.PixelHeight);
                                //previewFrame.CopyToBuffer(newBitmap.PixelBuffer);

                                //// encode frame
                                InMemoryRandomAccessStream ms = new InMemoryRandomAccessStream();
                                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);
                                encoder.SetSoftwareBitmap(previewFrame);
                                await encoder.FlushAsync();
                                xb = new byte[ms.Size];
                                var x = await ms.ReadAsync(xb.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);

                                frameheader = new byte[5];
                                frameheader[0] = 0xFD;                              //Header Start 
                                frameheader[1] = 0xFD;
                                frameheader[2] = 0xFD;
                                frameheader[3] = 0x00;                              //HEader Command    
                                frameheader[4] = 0x01;
                                _dw.WriteBuffer(frameheader.AsBuffer());


                                //_dw.WriteUInt32(newBitmap.PixelBuffer.Length);
                                //_dw.WriteUInt32((uint)xb.Count());
                                _dw.WriteInt32(previewFrame.PixelWidth);
                                _dw.WriteInt32(previewFrame.PixelHeight);
                                _dw.WriteInt32(jsonObject.ToString().Length);
                                _dw.WriteString(jsonObject.ToString());
                                //_dw.WriteBuffer(newBitmap.PixelBuffer);
                                _dw.WriteUInt32((uint)xb.Count());
                                _dw.WriteBuffer(xb.AsBuffer());

                                await _dw.StoreAsync();
                                //xb = null;
                                //previewFrame.Dispose();
                                //ms.Dispose();
                                //GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
                            }
                            catch (Exception e)
                            {
                            }
                        //});
                        //blocktransmission = true;
                    }

                }

                if (_isFaceDetected == true)
                {
                    //ApplyRedFilter(previewFrame);
                }

                //var sbSource = new SoftwareBitmapSource();
                //await sbSource.SetBitmapAsync(previewFrame);

                // Display it in the Image control
                //PreviewFrameImage.Source = sbSource;

                //    await SaveSoftwareBitmapAsync(previewFrame);
            //}
        }



        #endregion

        #region HandleSplitter
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

        private void Splitter_PaneClosed(SplitView sender, object args)
        {
            Settings.Visibility = Visibility.Collapsed;
        }
        #endregion HandleSplitter

        #region HelperFunctions

        private ObservableCollection<string> CameraDeviceList = new ObservableCollection<string>();
        private ObservableCollection<DeviceInformation> CameraDeviceInformation = new ObservableCollection<DeviceInformation>();

        /// <summary>
        /// Queries the available video capture devices to try and find one mounted on the desired panel
        /// </summary>
        /// <param name="desiredPanel">The panel on the device that the desired camera is mounted on</param>
        /// <returns>A DeviceInformation instance with a reference to the camera mounted on the desired panel if available,
        ///          any other camera if not, or null if no camera is available.</returns>
        private async Task<DeviceInformation> FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel desiredPanel)
        {
            // Get available devices for capturing pictures
            var allVideoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            CameraDeviceList.Clear();
            CameraDeviceInformation.Clear();

            string cameraDeviceInfoIndex = (string)localSettings.Values["CameraDeviceID"];

            DeviceInformation desiredDevice = null;

            foreach (DeviceInformation i in allVideoDevices)
            {

                string s = "";
                if (i.EnclosureLocation == null)
                {
                    s = i.Name;
                }
                else
                {
                    switch (i.EnclosureLocation.Panel)
                    {
                        case Windows.Devices.Enumeration.Panel.Back: { s = "(Back) " + i.Name; break; }
                        case Windows.Devices.Enumeration.Panel.Bottom: { s = "(Bottom) " + i.Name; break; }
                        case Windows.Devices.Enumeration.Panel.Front: { s = "(Front) " + i.Name; break; }
                        case Windows.Devices.Enumeration.Panel.Left: { s = "(Left) " + i.Name; break; }
                        case Windows.Devices.Enumeration.Panel.Right: { s = "(Right) " + i.Name; break; }
                        case Windows.Devices.Enumeration.Panel.Top: { s = "(Top) " + i.Name; break; }
                        default: { s = i.Name; break; }
                    }
                }
                CameraDeviceList.Add(s);
                CameraDeviceInformation.Add(i);
                if (cameraDeviceInfoIndex != null)
                {
                    if (i.Id.ToUpper() == cameraDeviceInfoIndex.ToUpper())
                    {
                        desiredDevice = i;
                    }
                }
                CameraList.ItemsSource = CameraDeviceList;
            }

            if (desiredDevice == null)
            {
                // Get the desired camera by panel
                desiredDevice = allVideoDevices.FirstOrDefault(x => x.EnclosureLocation != null && x.EnclosureLocation.Panel == desiredPanel);

                // If there is no device mounted on the desired panel, return the first device found
                desiredDevice = desiredDevice ?? allVideoDevices.FirstOrDefault();
            }

            SelectDeviceinList(desiredDevice, CameraDeviceInformation);
            return desiredDevice;
        }

        private bool blockcamerarestart = false;
        private bool blockcameramoderestart = false;

        private bool SelectDeviceinList(DeviceInformation desiredDevice, ObservableCollection<DeviceInformation> list)
        {
            int x = 0;

            string cameraDeviceInfoIndex = (string)localSettings.Values["CameraDeviceID"];
            if (cameraDeviceInfoIndex == null)
            {
                foreach (DeviceInformation i in list)
                {
                    if (i.Id == desiredDevice.Id)
                    {
                        localSettings.Values["CameraDeviceID"] = i.Id.ToUpper();
                        blockcamerarestart = true;
                        CameraList.SelectedIndex = x;
                    }
                    x++;
                }
            }
            else
            {
                foreach (DeviceInformation i in list)
                {
                    if (i.Id.ToUpper() == cameraDeviceInfoIndex)
                    {
                        localSettings.Values["CameraDeviceID"] = i.Id.ToUpper();
                        blockcamerarestart = true;
                        CameraList.SelectedIndex = x;
                    }
                    x++;
                }
            }



            return true;
        }

        private bool SelectDeviceModeinList(VideoEncodingProperties desiredDeviceMode, ObservableCollection<VideoEncodingProperties> list)
        {
            int x = 0;
            string cameraDeviceModeIndex = (string)localSettings.Values["CameraModeIndex"];
            if (cameraDeviceModeIndex == null)
            {
                foreach (VideoEncodingProperties i in list)
                {
                    if ((i.Height == desiredDeviceMode.Height) &&
                        (i.Width == desiredDeviceMode.Width) &&
                        (i.Subtype == desiredDeviceMode.Subtype) &&
                        (i.Type == desiredDeviceMode.Type)
                       )
                    {
                        localSettings.Values["CameraModeIndex"] = x.ToString();
                        blockcameramoderestart = true;
                        CameraModeList.SelectedIndex = x;
                    }
                    x++;
                }
            }
            else
            {
                try
                {
                    blockcameramoderestart = true;
                    CameraModeList.SelectedIndex = Convert.ToInt32(cameraDeviceModeIndex);
                }
                catch
                {
                    blockcameramoderestart = true;
                    localSettings.Values["CameraModeIndex"] = "0";
                    CameraModeList.SelectedIndex = 0;
                }
            }
            return true;
        }



        /// <summary>
        /// Converts the given orientation of the app on the screen to the corresponding rotation in degrees
        /// </summary>
        /// <param name="orientation">The orientation of the app on the screen</param>
        /// <returns>An orientation in degrees</returns>
        private static int ConvertDisplayOrientationToDegrees(DisplayOrientations orientation)
        {
            switch (orientation)
            {
                case DisplayOrientations.Portrait:
                    return 90;
                case DisplayOrientations.LandscapeFlipped:
                    return 180;
                case DisplayOrientations.PortraitFlipped:
                    return 270;
                case DisplayOrientations.Landscape:
                default:
                    return 0;
            }
        }


        #endregion

        private void S1Btn_Click(object sender, RoutedEventArgs e)
        {
            Button b = (Button)e.OriginalSource;

            if (b.Name == "S1plusBtn")
            {
                S1_offset = S1_offset + 1;
                S1ValueText.Text = S1_offset.ToString();
                localSettings.Values["S1Offset"] = S1_offset; 
            }
            if (b.Name == "S1minusBtn")
            {
                S1_offset = S1_offset - 1;
                S1ValueText.Text = S1_offset.ToString();
                localSettings.Values["S1Offset"] = S1_offset;
            }

            if (b.Name == "S2plusBtn")
            {
                S2_offset = S2_offset + 1;
                S2ValueText.Text = S2_offset.ToString();
                localSettings.Values["S2Offset"] = S2_offset;
            }
            if (b.Name == "S2minusBtn")
            {
                S2_offset = S2_offset - 1;
                S2ValueText.Text = S2_offset.ToString();
                localSettings.Values["S2Offset"] = S2_offset;
            }

            if (b.Name == "S3plusBtn")
            {
                S3_offset = S3_offset + 1;
                S3ValueText.Text = S3_offset.ToString();
                localSettings.Values["S3Offset"] = S3_offset;
            }
            if (b.Name == "S3minusBtn")
            {
                S3_offset = S3_offset - 1;
                S3ValueText.Text = S3_offset.ToString();
                localSettings.Values["S3Offset"] = S3_offset;
            }

            if (b.Name == "S4plusBtn")
            {
                S4_offset = S4_offset + 1;
                S4ValueText.Text = S4_offset.ToString();
                localSettings.Values["S4Offset"] = S4_offset;
            }
            if (b.Name == "S4minusBtn")
            {
                S4_offset = S4_offset - 1;
                S4ValueText.Text = S4_offset.ToString();
                localSettings.Values["S4Offset"] = S4_offset;
            }


        }
    }

    /// <summary>
    /// Helper class describing a NetworkAdapter and its associated IP address
    /// </summary>
    class LocalHostItem
    {
        public string DisplayString
        {
            get;
            private set;
        }

        public HostName LocalHost
        {
            get;
            private set;
        }

        public LocalHostItem(HostName localHostName)
        {
            if (localHostName == null)
            {
                throw new ArgumentNullException("localHostName");
            }

            if (localHostName.IPInformation == null)
            {
                throw new ArgumentException("Adapter information not found");
            }

            this.LocalHost = localHostName;
            this.DisplayString = "Address: " + localHostName.DisplayName +
                " Adapter: " + localHostName.IPInformation.NetworkAdapter.NetworkAdapterId;
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
