using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SunPet
{
    internal sealed class ClipFilterWindow : Window
    {
        private readonly AssetCatalog catalog;
        private readonly PlaybackSelector selector;
        private readonly Action save;
        private readonly List<CheckBox> choices = new List<CheckBox>();
        private readonly TextBlock selectionCount;
        private readonly TextBlock feedback;
        private bool refreshing;

        public ClipFilterWindow(AssetCatalog catalog, PlaybackSelector selector, Action save)
        {
            if (catalog == null) throw new ArgumentNullException("catalog");
            if (selector == null) throw new ArgumentNullException("selector");
            if (save == null) throw new ArgumentNullException("save");
            this.catalog = catalog;
            this.selector = selector;
            this.save = save;

            Title = "轮播内容 · SunPet";
            Width = 460;
            Height = 620;
            MinWidth = 400;
            MinHeight = 400;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            Background = Brushes.White;
            UseLayoutRounding = true;

            Grid layout = new Grid { Margin = new Thickness(20) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            selectionCount = new TextBlock
            {
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 33, 39)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            AutomationProperties.SetAutomationId(selectionCount, "SelectedClipCount");
            layout.Children.Add(selectionCount);

            TextBlock description = new TextBlock
            {
                Text = "取消勾选只影响随机轮播和接话；固定播放、手动挑一句不受影响。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(91, 96, 106)),
                FontSize = 12,
                LineHeight = 20,
                Margin = new Thickness(0, 0, 0, 16)
            };
            Grid.SetRow(description, 1);
            layout.Children.Add(description);

            StackPanel list = new StackPanel();
            foreach (Clip clip in catalog.Clips)
            {
                string label = clip.Id.ToString("00") + "  " + clip.Label;
                CheckBox choice = new CheckBox
                {
                    Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                    Tag = clip.Id,
                    Margin = new Thickness(8, 7, 8, 7),
                    Padding = new Thickness(5, 0, 0, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                AutomationProperties.SetName(choice, label);
                choice.Checked += OnChoiceChanged;
                choice.Unchecked += OnChoiceChanged;
                choices.Add(choice);
                list.Children.Add(choice);
            }
            ScrollViewer scroll = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Padding = new Thickness(2, 4, 2, 4)
            };
            Border listBorder = new Border
            {
                Child = scroll,
                BorderBrush = new SolidColorBrush(Color.FromRgb(222, 225, 230)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5)
            };
            Grid.SetRow(listBorder, 2);
            layout.Children.Add(listBorder);

            feedback = new TextBlock
            {
                MinHeight = 22,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(151, 78, 10)),
                Margin = new Thickness(0, 8, 0, 8)
            };
            Grid.SetRow(feedback, 3);
            layout.Children.Add(feedback);

            Grid buttons = new Grid();
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button restore = new Button
            {
                Content = "全部恢复",
                MinWidth = 96,
                Padding = new Thickness(14, 7, 14, 7),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            restore.Click += delegate
            {
                selector.EnableAll();
                save();
                RefreshChoices();
                feedback.Text = "已恢复全部语音。";
            };
            buttons.Children.Add(restore);
            Button done = new Button
            {
                Content = "完成",
                MinWidth = 96,
                Padding = new Thickness(14, 7, 14, 7),
                IsDefault = true
            };
            done.Click += delegate { Close(); };
            Grid.SetColumn(done, 1);
            buttons.Children.Add(done);
            Grid.SetRow(buttons, 4);
            layout.Children.Add(buttons);
            Content = layout;

            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            };
            Activated += delegate { RefreshChoices(); };
            RefreshChoices();
        }

        private void OnChoiceChanged(object sender, RoutedEventArgs e)
        {
            if (refreshing) return;
            CheckBox choice = (CheckBox)sender;
            int id = (int)choice.Tag;
            bool shouldBeSelected = !selector.IsExcluded(id);
            if (choice.IsChecked == shouldBeSelected) return;

            if (!selector.ToggleExcluded(id))
            {
                RefreshChoices();
                feedback.Text = "至少保留一条语音用于随机轮播。";
                return;
            }
            save();
            RefreshChoices();
            feedback.Text = "";
        }

        private void RefreshChoices()
        {
            refreshing = true;
            try
            {
                foreach (CheckBox choice in choices)
                    choice.IsChecked = !selector.IsExcluded((int)choice.Tag);
                selectionCount.Text = "已选 " + selector.AllowedCount + " / " + catalog.Clips.Length + " 句";
            }
            finally { refreshing = false; }
        }
    }
}
