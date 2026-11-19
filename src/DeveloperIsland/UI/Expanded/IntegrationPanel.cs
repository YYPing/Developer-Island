using DeveloperIsland.Core.Events.Sources;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DeveloperIsland.UI.Expanded;

/// <summary>Native compact panels. Configuration stays in a collapsed expander to keep daily use quiet.</summary>
internal sealed class IntegrationPanel : StackPanel
{
    private readonly IntegrationHost _host;
    private readonly ModuleId _module;
    private readonly TextBlock _status;
    private readonly StackPanel _items = new() { Spacing = 10 };
    private readonly TextBlock _chat;
    private readonly Button? _send;

    /// <summary>
    /// Animated glyphs currently on screen. Each one keeps compositor animations running, so they have
    /// to be stopped when the card is rebuilt rather than left animating a detached visual.
    /// </summary>
    private readonly List<Components.Atmosphere> _glyphs = [];

    public IntegrationPanel(IntegrationHost host, ModuleId module)
    {
        _host = host;
        _module = module;
        RequestedTheme = ElementTheme.Dark;
        Spacing = 12;
        Visibility = Visibility.Collapsed;
        _status = Label("");
        _chat = Label("");
        Children.Add(_status);
        if (module == ModuleId.AiChat)
        {
            Children.Add(_chat);
            var input = new TextBox { PlaceholderText = "问点什么…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 12000, MaxHeight = 100 };
            Children.Add(input);
            _send = Button("发送", async () => { var text = input.Text; await host.SendChatAsync(text); if (host.Text(module).StartsWith("Conversation")) input.Text = ""; });
            Children.Add(_send);
            Children.Add(Button("清空对话", host.ClearChat));
        }
        else if (module is ModuleId.Inbox or ModuleId.Feed or ModuleId.Weather or ModuleId.Markets)
        {
            Children.Add(_items);
        }

        if (module is ModuleId.Inbox or ModuleId.Feed)
        {
            Children.Add(Button(module == ModuleId.Inbox ? "全部标为已读" : "清空动态", () => { if (module == ModuleId.Inbox) host.MarkRead(); else host.Bus.Clear(); }));
            if (module == ModuleId.Inbox)
            {
                var input = new TextBox { PlaceholderText = "记一条本地消息", MaxLength = 2000 };
                Children.Add(input);
                Children.Add(Button("收进消息", () =>
                {
                    if (!string.IsNullOrWhiteSpace(input.Text)) host.AddMessage(new InboxItem(Guid.NewGuid().ToString(), "本地", "本地消息", input.Text, DateTimeOffset.Now));
                    input.Text = "";
                }));
            }
        }
        if (module is ModuleId.Inbox or ModuleId.Pipeline or ModuleId.Weather or ModuleId.Markets)
            Children.Add(Button("刷新", async () => await host.RefreshAsync(module)));
        var settings = BuildSettings();
        if (settings.Children.Count > 0) Children.Add(new Expander { Header = "连接设置", HorizontalAlignment = HorizontalAlignment.Stretch, Content = settings });
        host.Changed += Refresh;

        // Collapsing the island hides this panel but keeps it alive; the loops would keep running
        // against a visual nobody can see.
        Unloaded += (_, _) =>
        {
            foreach (var glyph in _glyphs)
            {
                glyph.Stop();
            }
        };
        Refresh();
    }

    private void Refresh()
    {
        if (_module == ModuleId.Feed)
        {
            _status.Text = "近期动态 · 本次运行保留 12 小时";
            _items.Children.Clear();
            foreach (var item in _host.Bus.Feed.Take(20))
            {
                _items.Children.Add(Label($"{item.Title}\n{item.Detail}\n{item.ReceivedAt.LocalDateTime:g}"));
                _items.Children.Add(Button("忽略", () => _host.Bus.Dismiss(item)));
            }
            if (_items.Children.Count == 0) _items.Children.Add(Label("没有新内容。"));
        }
        else if (_module == ModuleId.Inbox)
        {
            _status.Text = $"{_host.Inbox.UnreadCount} 条未读\n" + _host.Text(_module);
            _items.Children.Clear();
            foreach (var item in _host.Inbox.Items.Take(30))
            {
                _items.Children.Add(Label($"{(item.Read ? "" : "● ")}{item.Source} · {item.Title}\n{item.Summary}\n{item.At.LocalDateTime:g}"));
                if (Uri.TryCreate(item.Link, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                    _items.Children.Add(Button("打开消息", async () => await Launcher.LaunchUriAsync(uri)));
            }
        }
        else if (_module == ModuleId.Markets)
        {
            // A quote is laid out as a row, not as a paragraph: the numbers are what the eye scans
            // for, so they get the figure treatment while the symbol and time stay secondary.
            _items.Children.Clear();
            if (_host.Markets.Count == 0)
            {
                _items.Children.Add(EmptyState(_host.LastError(_module) ?? "还没有行情数据。刷新试试。"));
            }
            else
            {
                foreach (var quote in _host.Markets)
                {
                    _items.Children.Add(QuoteCard(quote));
                }


                _items.Children.Add(Caption($"腾讯行情 · 可能有延迟 · 抓取于 {_host.MarketsFetchedAt:HH:mm:ss}"));
            }
        }
        else if (_module == ModuleId.Weather)
        {
            // Stop the previous glyph's loops before dropping it: a compositor animation on a detached
            // visual keeps ticking, and a refresh every few minutes would accumulate them.
            foreach (var old in _glyphs)
            {
                old.Stop();
            }

            _glyphs.Clear();
            _items.Children.Clear();

            if (_host.Weather is not { } weather)
            {
                _items.Children.Add(EmptyState(_host.LastError(_module) ?? "还没有天气数据。刷新试试。"));
            }
            else
            {
                _items.Children.Add(WeatherCard(weather));
            }
        }
        else
        {
            _status.Text = _module == ModuleId.Agents
                ? _host.Text(_module) + "\nClaude hooks: " + AgentHooksSetup.Inspect(AgentHooksSetup.SettingsPath()).Connection + "\n其他 agent 可以发送本地上报。这里显示最后一次上报，不代表进程仍在运行。"
                : _host.Text(_module);
        }
        if (_module == ModuleId.AiChat)
        {
            _chat.Text = _host.ChatText;
            if (_send is not null) _send.IsEnabled = !_host.ChatBusy;
        }
    }

    private StackPanel BuildSettings()
    {
        var panel = new StackPanel { Spacing = 10, Width = 300 };
        void Field(string title, string value, Action<string> save)
        {
            var input = new TextBox { Header = title, Text = value };
            panel.Children.Add(input);
            panel.Children.Add(Button("Save " + title, () => { save(input.Text.Trim()); _status.Text = "Saved."; }));
        }
        void Secret(string title, string key)
        {
            var password = new PasswordBox { Header = title, PlaceholderText = "填入新值；留空则删除凭据" };
            panel.Children.Add(password);
            panel.Children.Add(Button("保存凭据", () => { _host.SaveSecret(key, password.Password); password.Password = ""; _status.Text = "凭据已存入 Windows 凭据管理器。"; }));
        }
        void Toggle(string title, bool value, Action<bool> save)
        {
            var toggle = new ToggleSwitch { Header = title, IsOn = value };
            toggle.Toggled += (_, _) => save(toggle.IsOn);
            panel.Children.Add(toggle);
        }
        switch (_module)
        {
            case ModuleId.AiChat:
                Field("接口地址", _host.Settings.ChatEndpoint, v => _host.Save(s => s.ChatEndpoint = v));
                Field("Model", _host.Settings.ChatModel, v => _host.Save(s => s.ChatModel = v));
                Secret("API Key（本机服务可不填）", "chat-api-key");
                panel.Children.Add(Label("OpenAI 兼容的 /chat/completions。只发送这个面板里的对话。走 HTTPS，或本机 localhost HTTP。"));
                break;
            case ModuleId.Agents:
                if (_host.IsDemo) { panel.Children.Add(Label("演示模式：不会改动真实的 Claude 设置。")); break; }
                panel.Children.Add(Button("连接 Claude hooks", () =>
                {
                    if (Environment.ProcessPath is { } exe) _status.Text = AgentHooksSetup.Connect(AgentHooksSetup.SettingsPath(), exe).ToString();
                }));
                panel.Children.Add(Button("断开 Claude hooks", () => _status.Text = AgentHooksSetup.Disconnect(AgentHooksSetup.SettingsPath()).ToString()));
                break;
            case ModuleId.Inbox:
                Toggle("Telegram Bot", _host.Settings.TelegramEnabled, v => _host.Save(s => s.TelegramEnabled = v));
                Secret("Telegram Bot Token", "telegram-bot");
                panel.Children.Add(Label("只显示 bot 有权限看到的群/频道消息，不是个人账号的全量未读。请用专用 bot，且不要同时配置 webhook 或其他 getUpdates 消费者。这里的未读是本地状态。不支持 Discord 个人未读，也不能用飞书 incoming webhook 读取消息。"));
                break;
            case ModuleId.Pipeline:
                Field("GitHub 仓库（owner/repo）", _host.Settings.PipelineRepository, v => _host.Save(s => s.PipelineRepository = v));
                Toggle("Docker（可选）", _host.Settings.DockerEnabled, v => _host.Save(s => s.DockerEnabled = v));
                panel.Children.Add(Label("使用你已登录的 gh。本地命令可以这样上报：DeveloperIsland.exe --report pipeline \"Build\" \"Passed\"（失败时加 --failed）。不会从 git 改动去猜构建结果。"));
                break;
            case ModuleId.Weather:
                // Read-only here on purpose: the city is set in Settings. Showing the value keeps the
                // panel answerable ("which city is this?") without putting a text field in a capsule
                // that retracts as soon as the pointer leaves.
                panel.Children.Add(Label($"当前城市：{_host.Settings.WeatherLocation}\n要换城市，到 设置 → 模块 → 天气与行情。"));
                break;
            case ModuleId.Markets:
                panel.Children.Add(Label($"当前代码：{_host.Settings.MarketSymbols}\n要改代码，到 设置 → 模块 → 天气与行情。"));
                break;
        }
        return panel;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12,
        Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11,
        Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
    };

    /// <summary>A quiet line for "nothing yet" and for a failed refresh, instead of an empty panel.</summary>
    private static TextBlock EmptyState(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, IsTextSelectionEnabled = true,
        Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
        Margin = new Thickness(2, 4, 2, 0),
    };

    /// <summary>
    /// One quote. A quote is an instrument reading, not a paragraph: the row carries the day's range so
    /// the move has a position instead of only a sign.
    /// </summary>
    private static Border QuoteCard(MarketReading quote)
    {
        var low = quote.HasRange ? quote.Low : 0;
        var high = quote.HasRange ? quote.High : 0;

        var card = Card(new Components.QuoteRow(quote, low, high));
        Animations.Motion.FadeIn(card, delay: TimeSpan.FromMilliseconds(30), fromY: 4);
        return card;
    }

    /// <summary>
    /// The weather as a reading: the animated atmosphere on the left, the figure on the right. The
    /// glyph is not an icon of the weather but the weather's own motion seen through a small window.
    /// </summary>
    private Border WeatherCard(WeatherReading weather)
    {
        var place = new TextBlock
        {
            Text = weather.Location,
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        var temperature = new TextBlock
        {
            Text = $"{weather.Temperature:0.#}°",
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            Margin = new Thickness(0, 1, 0, 0),
        };

        var wind = new TextBlock
        {
            Text = $"{weather.Wind:0.#} m/s",
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            Margin = new Thickness(0, 1, 0, 0),
        };

        var observed = new TextBlock
        {
            Text = weather.ObservedAt,
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            Margin = new Thickness(0, 3, 0, 0),
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(place);
        text.Children.Add(temperature);
        text.Children.Add(wind);
        text.Children.Add(observed);

        var glyph = new Components.Atmosphere(weather.Condition, weather.IsDay, weather.Wind);
        _glyphs.Add(glyph);

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(glyph);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(Caption("中国气象局 · 实况"));

        var card = Card(stack);
        Animations.Motion.FadeIn(card, delay: TimeSpan.FromMilliseconds(40), fromY: 6);

        return card;
    }

    /// <summary>The island's standard raised well: one radius step under the panel, no border.</summary>
    private static Border Card(UIElement content) => new()
    {
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(12, 10, 12, 10),
        Background = (Brush)Application.Current.Resources["SurfaceRaisedBrush"],
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Child = content,
    };
    private Button Button(string text, Action action)
    {
        var button = new Button { Style = (Style)Application.Current.Resources["IslandPillButtonStyle"], Content = text };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { _status.Text = "Could not save: " + ex.GetType().Name; }
        };
        return button;
    }
}
