using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The User Control item template is documented at http://go.microsoft.com/fwlink/?LinkId=234236

namespace ROV10
{
    public sealed partial class JoystickControl : UserControl
    {
        public enum Mode { FullXY, Xonly, Yonly, Gear };
        public enum XReleaseMode { ReturnToZero, Sticky };
        public enum YReleaseMode { ReturnToZero, Sticky };
        public enum ExitMode { Collapse, Sticky };

        private Mode _mode = Mode.FullXY;
        public Mode mode { get { return _mode; } set { _mode = value; this.UpdateMode(); } }

        private XReleaseMode _xReleaseMode = XReleaseMode.ReturnToZero;
        public XReleaseMode xReleaseMode { get { return _xReleaseMode; } set { _xReleaseMode = value; } }

        private YReleaseMode _yReleaseMode = YReleaseMode.ReturnToZero;
        public YReleaseMode yReleaseMode { get { return _yReleaseMode; } set { _yReleaseMode = value; } }

        private ExitMode _exitMode = ExitMode.Collapse;

        public ExitMode exitMode { get { return _exitMode; } set { _exitMode = value; } }

        private double _Xp = 0;
        public double X { get { return _Xp; } set { _Xp = value; } }

        private double _Yp = 0;
        public double Y { get { return _Yp; } set { _Yp = value; } }

        public bool isInRange = false;
        public bool isExited = false;
        public bool removeOnExit = false;


        private void UpdateMode()
        {
            if (_mode == Mode.FullXY)
            {
                XY.Visibility = Visibility.Visible;
                SingleX.Visibility = Visibility.Collapsed;
                SingleY.Visibility = Visibility.Collapsed;
                Gearbox.Visibility = Visibility.Collapsed;
            }
            if (_mode == Mode.Xonly)
            {
                XY.Visibility = Visibility.Collapsed;
                SingleX.Visibility = Visibility.Visible;
                SingleY.Visibility = Visibility.Collapsed;
                Gearbox.Visibility = Visibility.Collapsed;
            }
            if (_mode == Mode.Yonly)
            {
                XY.Visibility = Visibility.Collapsed;
                SingleX.Visibility = Visibility.Collapsed;
                SingleY.Visibility = Visibility.Visible;
                Gearbox.Visibility = Visibility.Collapsed;
            }
            if (_mode == Mode.Gear)
            {
                XY.Visibility = Visibility.Collapsed;
                SingleX.Visibility = Visibility.Collapsed;
                SingleY.Visibility = Visibility.Collapsed;
                Gearbox.Visibility = Visibility.Visible;
            }
        }

        public JoystickControl()
        {
            this.InitializeComponent();
            this.UpdateMode();
        }

        private void Ellipse_PointerPressed(object sender, PointerRoutedEventArgs e)
        {

        }

        private void NormalizeKnopPosition(PointerPoint pt)
        {
            if (mode == Mode.FullXY)
            {
                knobPosition.X = pt.Position.X - Base.Width / 2;
                knobPosition.Y = pt.Position.Y - Base.Height / 2;

                if (pt.Position.X < 0) { knobPosition.X = (Base.Width / 2) * -1; };
                if (pt.Position.X > Base.Width) { knobPosition.X = Base.Width / 2; };

                if (pt.Position.Y < 0) { knobPosition.Y = (Base.Height / 2) * -1; };
                if (pt.Position.Y > Base.Height) { knobPosition.Y = Base.Height / 2; };

                X = 100 * (knobPosition.X) / (Base.Width / 2);
                Y = 100 * (knobPosition.Y) / (Base.Height / 2);
            }
            if (mode == Mode.Xonly)
            {
                knobPosition.X = pt.Position.X - Base.Width / 2;
                knobPosition.Y = 0;

                if (pt.Position.X < 0) { knobPosition.X = (Base.Width / 2) * -1; };
                if (pt.Position.X > Base.Width) { knobPosition.X = Base.Width / 2; };

                X = 100 * (pt.Position.X - Base.Width / 2) / (Base.Width / 2);
                Y = 0;
            }
            if (mode == Mode.Yonly)
            {
                knobPosition.X = 0;
                knobPosition.Y = pt.Position.Y - Base.Height / 2;

                if (pt.Position.Y < 0) { knobPosition.Y = (Base.Height / 2) * -1; };
                if (pt.Position.Y > Base.Height) { knobPosition.Y = Base.Height / 2; };

                X = 0;
                Y = 100 * (pt.Position.Y - Base.Height / 2) / (Base.Height / 2);
            }
            if (mode == Mode.Gear)
            {
                double _knobPositionX = pt.Position.X - Base.Width / 2;
                double _knobPositionY = pt.Position.Y - Base.Height / 2;

                if (pt.Position.X < 0) { _knobPositionX = (Base.Width / 2) * -1; };
                if (pt.Position.X > Base.Width) { _knobPositionX = Base.Width / 2; };

                if (pt.Position.Y < 0) { _knobPositionY = (Base.Height / 2) * -1; };
                if (pt.Position.Y > Base.Height) { _knobPositionY = Base.Height / 2; };

                //knobPosition.X = _knobPositionX;
                //knobPosition.Y = _knobPositionY; 

                double _X = 100 * (pt.Position.X - Base.Width / 2) / (Base.Width / 2);
                double _Y = 100 * (pt.Position.Y - Base.Height / 2) / (Base.Height / 2);
                if ((_X > -20) && (_X < 20))
                {
                    X = 0;
                    knobPosition.X = 0;
                    Y = _Y;
                    knobPosition.Y = _knobPositionY;
                }
                if (_X < -40)
                {
                    X = -100;
                    knobPosition.X = (Base.Width / 2) * X * 0.765 / 100;
                    Y = _Y;
                    knobPosition.Y = _knobPositionY;
                }
                if (_X > 40)
                {
                    X = 100;
                    knobPosition.X = (Base.Width / 2) * X * 0.765 / 100;
                    Y = _Y;
                    knobPosition.Y = _knobPositionY;
                }

            }
        }

        public uint _PointerId = 0;
        public void Base_PointerPressed(object sender, PointerRoutedEventArgs e)
        {

            Windows.UI.Xaml.Input.Pointer ptr = e.Pointer;


            if (_PointerId == 0)
            {
                isExited = false;
                isInRange = true;
                _PointerId = e.Pointer.PointerId;
                PointerPoint pt = e.GetCurrentPoint(Base);

                NormalizeKnopPosition(pt);
            }
        }

        public void Base_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if ((_PointerId == e.Pointer.PointerId) || (isExited == true))
            {
                if (mode == Mode.Gear)
                {
                    Y = 0;
                    centerKnobY.Begin();
                    _PointerId = 0;
                    isInRange = false;
                }
                else
                {
                    X = 0;
                    Y = 0;
                    centerKnobX.Begin();
                    centerKnobY.Begin();
                    _PointerId = 0;
                    isInRange = false;
                    if (exitMode == ExitMode.Sticky)
                    {
                    }
                    else
                    {
                        this.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        public void Base_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if ((_PointerId == e.Pointer.PointerId) || (isExited == true))
            {
                Windows.UI.Xaml.Input.Pointer ptr = e.Pointer;
                PointerPoint pt = e.GetCurrentPoint(Base);

                NormalizeKnopPosition(pt);
            }
        }


        private void Base_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_PointerId == e.Pointer.PointerId)
            {
                isInRange = false;
                isExited = true;
                if (mode == Mode.Gear)
                {
                    Y = 0;
                    centerKnobY.Begin();
                    _PointerId = 0;
                }
                else
                {
                    if (exitMode == ExitMode.Sticky)
                    {
                    }
                    else
                    {
                        X = 0;
                        Y = 0;
                        centerKnob.Begin();
                        _PointerId = 0;
                        this.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }
    }

}
