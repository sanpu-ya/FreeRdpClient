using FreeRdp.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace FreeRdpClient.Services;

/// <summary>Prompts shown during connection. Only one ContentDialog may be open per window.</summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            using var reg = token.Register(() => dialog.DispatcherQueue.TryEnqueue(dialog.Hide));
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<(RdpCredentials? Credentials, bool Save)> AskCredentialsAsync(
        XamlRoot root, string target, RdpAuthenticationRequest request, bool offerSave, CancellationToken token)
    {
        var user = new TextBox { Header = "ユーザー名", Text = request.UserName ?? "", PlaceholderText = "user または DOMAIN\\user" };
        var domain = new TextBox { Header = "ドメイン (省略可)", Text = request.Domain ?? "" };
        var password = new PasswordBox { Header = "パスワード" };
        var save = new CheckBox { Content = "パスワードを保存する", Visibility = offerSave ? Visibility.Visible : Visibility.Collapsed };

        var title = request.Reason switch
        {
            RdpAuthReason.GatewayHttp or RdpAuthReason.GatewayRdg or RdpAuthReason.GatewayRpc => "ゲートウェイの資格情報",
            RdpAuthReason.SmartcardPin => "スマートカードの PIN",
            _ => "資格情報を入力してください",
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = target, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        if (request.Reason != RdpAuthReason.SmartcardPin)
        {
            panel.Children.Add(user);
            panel.Children.Add(domain);
        }
        panel.Children.Add(password);
        panel.Children.Add(save);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) =>
        {
            if (request.Reason == RdpAuthReason.SmartcardPin || !string.IsNullOrEmpty(user.Text))
                password.Focus(FocusState.Programmatic);
            else
                user.Focus(FocusState.Programmatic);
        };

        if (await ShowAsync(dialog, token) != ContentDialogResult.Primary)
            return (null, false);

        var name = user.Text.Trim();
        var dom = string.IsNullOrWhiteSpace(domain.Text) ? null : domain.Text.Trim();
        var separator = name.IndexOf('\\');
        if (dom == null && separator > 0)
        {
            dom = name[..separator];
            name = name[(separator + 1)..];
        }

        return (new RdpCredentials(name, password.Password, dom), save.IsChecked == true);
    }

    public static async Task<CertificateDecision> VerifyCertificateAsync(XamlRoot root, RdpCertificateRequest request, CancellationToken token)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 520 };

        var intro = request.Changed
            ? "警告: このサーバーの証明書が以前と変わっています。中間者攻撃の可能性があります。"
            : "リモート コンピューターの ID を確認できません。接続を続行しますか?";
        panel.Children.Add(new TextBlock
        {
            Text = intro,
            TextWrapping = TextWrapping.Wrap,
            Foreground = request.Changed ? new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) : null,
        });

        if (request.IsNameMismatch)
            panel.Children.Add(new TextBlock { Text = "証明書の名前が接続先のホスト名と一致しません。", TextWrapping = TextWrapping.Wrap });

        var details = new RichTextBlock { IsTextSelectionEnabled = true, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        void Row(string label, string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            var p = new Paragraph();
            p.Inlines.Add(new Run { Text = label + ": ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            p.Inlines.Add(new Run { Text = value });
            details.Blocks.Add(p);
        }
        Row(request.IsGateway ? "ゲートウェイ" : "ホスト", $"{request.Host}:{request.Port}");
        Row("共通名", request.CommonName);
        Row("サブジェクト", request.Subject);
        Row("発行者", request.Issuer);
        Row("フィンガープリント", request.Fingerprint);
        Row("以前のフィンガープリント", request.OldFingerprint);
        panel.Children.Add(details);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "証明書の確認",
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            PrimaryButtonText = "常に信頼する",
            SecondaryButtonText = "今回のみ接続",
            CloseButtonText = "接続しない",
            DefaultButton = ContentDialogButton.Close,
        };

        return await ShowAsync(dialog, token) switch
        {
            ContentDialogResult.Primary => CertificateDecision.AcceptPermanently,
            ContentDialogResult.Secondary => CertificateDecision.AcceptOnce,
            _ => CertificateDecision.Reject,
        };
    }

    public static async Task<bool> ShowGatewayMessageAsync(XamlRoot root, RdpGatewayMessage message, CancellationToken token)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "ゲートウェイからのメッセージ",
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = message.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                MaxHeight = 400,
            },
            PrimaryButtonText = message.ConsentMandatory ? "同意する" : "OK",
            CloseButtonText = message.ConsentMandatory ? "同意しない" : null,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog, token) == ContentDialogResult.Primary || !message.ConsentMandatory;
    }

    public static async Task ShowErrorAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "閉じる",
        };
        await ShowAsync(dialog, CancellationToken.None);
    }
}
