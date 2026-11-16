using System;
using System.Drawing;
using System.Windows.Forms;

namespace KeepClear
{
    // Fixed logical dimensions, with a single 96-DPI design baseline.
    public class DpiFixedForm : Form
    {
        private readonly Size logicalClient;
        private bool arranging;
        protected float LayoutScale { get { return IsHandleCreated ? DeviceDpi / 96f : 1f; } }
        protected int Px(int value) { return (int)Math.Round(value * LayoutScale); }
        protected void SetLogicalBounds(Control control, int x, int y, int width, int height)
        {
            control.SetBounds(Px(x), Px(y), Px(width), Px(height));
        }
        protected DpiFixedForm(Size logicalClient)
        {
            this.logicalClient = logicalClient;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = logicalClient;
        }
        protected virtual void ArrangeForDpi() { }
        private void FixLayout()
        {
            if (arranging || IsDisposed) return;
            arranging = true;
            try
            {
                MinimumSize = MaximumSize = Size.Empty;
                ClientSize = new Size(Px(logicalClient.Width), Px(logicalClient.Height));
                ArrangeForDpi();
                PerformLayout();
                MinimumSize = MaximumSize = Size;
                Diagnostics.Write("ui-dpi", GetType().Name + " dpi=" + DeviceDpi + " design=" + AutoScaleDimensions + " current=" + CurrentAutoScaleDimensions + " client=" + ClientSize + " font_pt=" + Font.SizeInPoints);
            }
            finally { arranging = false; }
        }
        protected override void OnLoad(EventArgs e)
        {
            ResumeLayout(true);
            base.OnLoad(e);
            FixLayout();
        }
        protected override void OnShown(EventArgs e)
        {
            FixLayout();
            base.OnShown(e);
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            MinimumSize = MaximumSize = Size.Empty;
            base.OnDpiChanged(e);
            FixLayout();
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)(() => { if (!IsDisposed) FixLayout(); }));
        }
    }
}
