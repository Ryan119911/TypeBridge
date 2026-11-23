using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace TypeBridgeApp
{
    /// <summary>
    /// 设置窗口和安装程序共用的一套轻量 WPF 控件，避免两边各画一遍。
    /// </summary>
    internal static class WpfUi
    {
        internal static readonly Color Ink = Color.FromRgb(16, 22, 34);
        internal static readonly Color Sub = Color.FromRgb(82, 96, 118);
        internal static readonly Color Line = Color.FromRgb(230, 236, 246);
        internal static readonly Color FieldBg = Color.FromRgb(246, 249, 254);
        internal static readonly Color Accent = Color.FromRgb(61, 126, 240);

        /// <summary>开关 + 标题 + 说明的一行。</summary>
        internal static UIElement ToggleRow(Switch toggle, string title, string detail)
        {
            Grid row = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(toggle);

            StackPanel text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                Foreground = new SolidColorBrush(Ink)
            });
            TextBlock sub = Note(detail);
            sub.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(sub);

            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            return row;
        }

        internal static UIElement Card(string title, StackPanel body)
        {
            body.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(52, 66, 92)),
                Margin = new Thickness(0, 0, 0, 12)
            });

            Grid host = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            Border shadowLayer = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = Brushes.White,
                Effect = Shadow(18, 3, 0.08)
            };
            Border card = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = Brushes.White,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Line),
                Padding = new Thickness(16, 12, 16, 14),
                Child = body
            };
            host.Children.Add(shadowLayer);
            host.Children.Add(card);
            return host;
        }

        internal static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = new SolidColorBrush(Sub),
                Margin = new Thickness(0, 0, 0, 6)
            };
        }

        internal static TextBlock Note(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
        }

        internal static UIElement Field(TextBox box)
        {
            Border frame = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(FieldBg),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 233, 245)),
                Padding = new Thickness(10, 7, 10, 7)
            };
            box.BorderThickness = new Thickness(0);
            box.Background = Brushes.Transparent;
            box.FontSize = 14;
            box.Foreground = new SolidColorBrush(Ink);
            box.VerticalContentAlignment = VerticalAlignment.Center;
            frame.Child = box;
            return frame;
        }

        internal static UIElement Row(string label, UIElement control, double labelWidth)
        {
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            TextBlock text = new TextBlock
            {
                Text = label,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(52, 66, 92)),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(text);
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            return row;
        }

        internal static Button SecondaryButton(string text)
        {
            Button button = new Button
            {
                Content = text,
                Height = 38,
                Padding = new Thickness(16, 0, 16, 0),
                FontSize = 13,
                Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush(Color.FromRgb(58, 72, 96)),
                Background = Brushes.White,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(216, 224, 238)),
                Template = RoundButtonTemplate()
            };
            AddHoverOpacity(button);
            return button;
        }

        /// <summary>主按钮（保存 / 安装）：实心蓝底。</summary>
        internal static Button PrimaryButton(string text)
        {
            Button button = new Button
            {
                Content = text,
                Height = 38,
                Padding = new Thickness(18, 0, 18, 0),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Accent),
                BorderThickness = new Thickness(0),
                Template = RoundButtonTemplate()
            };
            AddHoverOpacity(button);
            return button;
        }

        internal static ControlTemplate RoundButtonTemplate(double contentMargin = 16)
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(contentMargin, 0, contentMargin, 0));
            border.AppendChild(presenter);

            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        internal static void AddHoverOpacity(Button button)
        {
            Style style = new Style(typeof(Button));
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82));
            style.Triggers.Add(hover);
            button.Style = style;
        }

        internal static DropShadowEffect Shadow(double blur, double depth, double opacity)
        {
            return new DropShadowEffect
            {
                BlurRadius = blur,
                ShadowDepth = depth,
                Direction = 270,
                Opacity = opacity,
                Color = Color.FromRgb(23, 43, 77)
            };
        }

        /// <summary>胶囊开关。</summary>
        internal sealed class Switch : Grid
        {
            private readonly Border track = new Border
            {
                Width = 46,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Color.FromRgb(214, 222, 236))
            };
            private readonly Ellipse knob = new Ellipse
            {
                Width = 20,
                Height = 20,
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(3, 0, 0, 0),
                Effect = Shadow(6, 1, 0.25)
            };

            private bool on;
            internal event EventHandler Toggled;

            internal bool IsOn
            {
                get { return on; }
                set { on = value; Apply(false); }
            }

            internal Switch()
            {
                Width = 46;
                Height = 26;
                Cursor = Cursors.Hand;
                VerticalAlignment = VerticalAlignment.Center;
                Children.Add(track);
                Children.Add(knob);
                MouseLeftButtonUp += delegate
                {
                    on = !on;
                    Apply(true);
                    EventHandler handler = Toggled;
                    if (handler != null) handler(this, EventArgs.Empty);
                };
            }

            private void Apply(bool animate)
            {
                Color target = on ? Accent : Color.FromRgb(214, 222, 236);
                if (animate)
                {
                    ColorAnimation color = new ColorAnimation(target, TimeSpan.FromMilliseconds(140));
                    track.Background = new SolidColorBrush(((SolidColorBrush)track.Background).Color);
                    track.Background.BeginAnimation(SolidColorBrush.ColorProperty, color);
                }
                else
                {
                    track.Background = new SolidColorBrush(target);
                }

                double shift = on ? 20 : 0;
                if (animate)
                {
                    TranslateTransform transform = knob.RenderTransform as TranslateTransform ?? new TranslateTransform();
                    knob.RenderTransform = transform;
                    transform.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(shift, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                }
                else
                {
                    knob.RenderTransform = new TranslateTransform(shift, 0);
                }
            }
        }
    }
}
