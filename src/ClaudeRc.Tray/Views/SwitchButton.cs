using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ClaudeRc.Tray.Views;

/// <summary>
/// The row's ON/OFF switch. A <see cref="ToggleButton"/> so UI Automation sees a toggle with a
/// toggle state, but a click never flips <see cref="ToggleButton.IsChecked"/> itself: it only
/// runs the command (which asks the host), and the switch shows the host's state through the
/// one-way <c>IsChecked</c> binding when the host says so.
/// </summary>
internal sealed class SwitchButton : ToggleButton
{
    /// <summary>Does not flip the state; the binding does when the host reports the change.</summary>
    protected override void OnToggle()
    {
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new SwitchButtonAutomationPeer(this);

    /// <summary>A UI Automation Toggle: the same as a click (runs the command).</summary>
    internal void ToggleFromAutomation() => OnClick();

    private sealed class SwitchButtonAutomationPeer(SwitchButton owner) : ToggleButtonAutomationPeer(owner), IToggleProvider
    {
        ToggleState IToggleProvider.ToggleState => owner.IsChecked switch
        {
            true => ToggleState.On,
            false => ToggleState.Off,
            _ => ToggleState.Indeterminate,
        };

        void IToggleProvider.Toggle()
        {
            if (!IsEnabled())
                throw new ElementNotEnabledException();
            // Asynchronously, as ButtonAutomationPeer.Invoke does: the click may show a modal
            // confirmation, and a synchronous call would block the automation client until it closes.
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, owner.ToggleFromAutomation);
        }
    }
}
