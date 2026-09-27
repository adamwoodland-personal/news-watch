using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;

namespace NewsWatch;

/// <summary>
/// Digits-only text box with spinner chevrons. Up/Down keys and the mouse
/// wheel (while focused) also step the value; arrows clamp to Min/Max.
/// Exposes Text so callers validate exactly as they did with a TextBox.
/// </summary>
public partial class NumberBox : UserControl
{
    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(int), typeof(NumberBox), new PropertyMetadata(0));
    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(NumberBox), new PropertyMetadata(int.MaxValue));
    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(int), typeof(NumberBox), new PropertyMetadata(1));

    public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Step { get => (int)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    public string Text
    {
        get => Input.Text;
        set => Input.Text = value;
    }

    public NumberBox()
    {
        InitializeComponent();
        ValidationHelpers.MakeNumeric(Input);

        Input.GotKeyboardFocus += (_, _) => Frame.BorderBrush = (Brush)FindResource("AccentBrush");
        Input.LostKeyboardFocus += (_, _) => Frame.BorderBrush = (Brush)FindResource("BorderBrush");

        Input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Up) { Nudge(+1); e.Handled = true; }
            else if (e.Key == Key.Down) { Nudge(-1); e.Handled = true; }
        };
        // Wheel only while focused, so scrolling a dialog doesn't spin values.
        PreviewMouseWheel += (_, e) =>
        {
            if (!Input.IsKeyboardFocused) return;
            Nudge(e.Delta > 0 ? +1 : -1);
            e.Handled = true;
        };
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Nudge(+1);
    private void Down_Click(object sender, RoutedEventArgs e) => Nudge(-1);

    private void Nudge(int direction)
    {
        var current = int.TryParse(Input.Text, out var n) ? n : Minimum;
        var next = Math.Clamp((long)current + (long)direction * Step, Minimum, Maximum);
        Input.Text = next.ToString();
        Input.CaretIndex = Input.Text.Length;
        Input.Focus();
    }
}
