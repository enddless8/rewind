using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ComboBox = System.Windows.Controls.ComboBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Replay;

// Keep the standard ComboBox interaction, except focus-driven scrolling on hover.
public class ApplicationSourceComboBox : ComboBox
{
    protected override DependencyObject GetContainerForItemOverride() => new HoverStableItem();

    private sealed class HoverStableItem : ComboBoxItem
    {
        private bool passiveMouseEnter;

        public HoverStableItem()
        {
            RequestBringIntoView += (_, e) =>
            {
                if (passiveMouseEnter && ReferenceEquals(e.TargetObject, this)) e.Handled = true;
            };
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            // ComboBoxItem highlights and focuses itself here. FrameworkElement's
            // synchronous focus request otherwise scrolls a partially visible row.
            var previous = passiveMouseEnter;
            passiveMouseEnter = e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released;
            try { base.OnMouseEnter(e); }
            finally { passiveMouseEnter = previous; }
        }
    }
}
