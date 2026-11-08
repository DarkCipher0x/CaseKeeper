using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;
using CaseKeeper.UI;

namespace CaseKeeper.Views;

/// <summary>
/// Settings → AI: which AI CaseKeeper uses (the local AI, or Anthropic, OpenAI or either on Microsoft Foundry), with
/// one-click setup for each. Cloud: paste a key, Connect checks it and picks the recommended quick model. Local: install
/// Ollama (the website copy uses winget; the Store copy opens the download page, as Store apps mustn't install programs)
/// and download the recommended model.
/// </summary>
public partial class MainWindow
{
    private ComboBox _providerPicker;
    private StackPanel _providerPanel, _localAiPanel;
    private TextBlock _providerStatus;

    private UIElement AiProviderSection(StackPanel localPanel)
    {
        _localAiPanel = localPanel;
        var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        sp.Children.Add(new TextBlock { Text = "WHICH AI TO USE", Style = Ui.Style("Label"), Margin = new Thickness(0, 6, 0, 2) });
        _providerPicker = new ComboBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = AiProviders.All.Select(p => p.Name).ToList() };
        AutomationProperties.SetName(_providerPicker, "Which AI to use");
        _providerPicker.SelectedIndex = Math.Max(0, AiProviders.All.ToList().FindIndex(p => p.Id == AiService.Provider));
        _providerPicker.SelectionChanged += (_, _) =>
        {
            var p = AiProviders.All[Math.Max(0, _providerPicker.SelectedIndex)];
            if (p.Id == AiService.Provider) return;
            Db.SetSetting("ai.provider", p.Id);
            Db.Audit("settings.change", "settings", null, "AI service chosen", p.Name);
            BuildProviderPanel();
            _aiView?.Reload();
        };
        sp.Children.Add(_providerPicker);
        _providerPanel = new StackPanel();
        sp.Children.Add(_providerPanel);
        _providerStatus = Ui.Muted("", 12.5);
        _providerStatus.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        AutomationProperties.SetLiveSetting(_providerStatus, AutomationLiveSetting.Polite);
        sp.Children.Add(_providerStatus);
        BuildProviderPanel();
        return sp;
    }

    private void BuildProviderPanel()
    {
        var p = AiProviders.Get(AiService.Provider);
        _providerPanel.Children.Clear();
        _providerStatus.Text = "";
        _localAiPanel.Visibility = p.IsCloud ? Visibility.Collapsed : Visibility.Visible;
        _providerPanel.Children.Add(Ui.Muted(p.About, 12));
        if (!p.IsCloud) { BuildLocalSetup(); return; }

        var keyBox = new PasswordBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(keyBox, $"{p.Name} key");
        var keyRow = new WrapPanel();
        keyRow.Children.Add(keyBox);
        var getKey = Ui.Button(p.IsFoundry ? "Open Microsoft Foundry" : "Get a key", null, (_, _) => OpenLink(p.KeyPage), tooltip: "Opens the provider's page in your browser");
        getKey.Margin = new Thickness(8, 0, 0, 0);
        keyRow.Children.Add(getKey);
        var clear = Ui.Button("Remove key", null, (_, _) =>
        {
            AiService.SetProviderKey(p.Id, "");
            Db.Audit("settings.change", "settings", null, $"{p.Name} key removed");
            BuildProviderPanel();
            _providerStatus.Text = "Key removed.";
        });
        clear.Margin = new Thickness(8, 0, 0, 0);
        if (AiService.HasProviderKey(p.Id)) keyRow.Children.Add(clear);
        _providerPanel.Children.Add(Labelled(p.IsFoundry ? "Foundry key" : "API key", keyRow,
            (AiService.HasProviderKey(p.Id) ? "A key is saved ✓. Paste a new one to replace it. " : "") +
            "Stored encrypted with Windows (DPAPI) for your account only. Never included in backups' settings, exports or logs."));

        TextBox resource = null;
        if (p.IsFoundry)
        {
            resource = new TextBox { Text = AiService.ProviderResource(p.Id), Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(resource, "Foundry resource");
            _providerPanel.Children.Add(Labelled("Foundry resource (name or endpoint)", resource,
                p.Family == "anthropic" ? "For example my-resource, or https://my-resource.services.ai.azure.com/anthropic/"
                                        : "For example my-resource, or https://my-resource.openai.azure.com/openai/v1/"));
        }

        var model = new ComboBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left, IsEditable = true, Text = AiService.ProviderModel(p.Id) };
        foreach (var (id, _) in p.Models) model.Items.Add(id);
        AutomationProperties.SetName(model, p.IsFoundry ? "Deployment name" : "Model");
        _providerPanel.Children.Add(Labelled(p.IsFoundry ? "Model deployment name" : "Model", model,
            string.Join("\n", p.Models.Select(m => m.Label)) + (p.IsFoundry ? "\nOn Foundry, type the name you gave the deployment." : "")));

        var effort = new ComboBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = p.Efforts.Select(e => e.Label).ToList() };
        effort.SelectedIndex = Math.Max(0, p.Efforts.ToList().FindIndex(e => e.Id == AiService.ProviderEffort(p.Id)));
        AutomationProperties.SetName(effort, "Reasoning effort");
        _providerPanel.Children.Add(Labelled("Reasoning effort", effort,
            "How long the model thinks before answering. Low is quick and cheap, and plenty for sorting and summarising documents."));

        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(Ui.Button("Connect", "", async (_, _) => await ConnectCloudAsync(p, keyBox, resource, model, effort), primary: true,
            tooltip: "Saves the key, checks it works with a tiny test request (no case material), and picks the recommended model"));
        row.Children.Add(Ui.Button("Use recommended", null, (_, _) =>
        {
            model.Text = p.DefaultModel;
            effort.SelectedIndex = Math.Max(0, p.Efforts.ToList().FindIndex(e => e.Id == p.DefaultEffort));
            SaveCloudChoice(p, model.Text, p.DefaultEffort);
            _providerStatus.Text = $"Recommended: {p.DefaultModel}, {p.DefaultEffort} effort.";
        }));
        _providerPanel.Children.Add(row);
        model.LostFocus += (_, _) => SaveCloudChoice(p, model.Text, p.Efforts[Math.Max(0, effort.SelectedIndex)].Id);
        effort.SelectionChanged += (_, _) => SaveCloudChoice(p, model.Text, p.Efforts[Math.Max(0, effort.SelectedIndex)].Id);

        _providerPanel.Children.Add(SettingCheck("Mask names and personal details before sending (recommended)", "ai.cloud.mask", "1",
            on => { if (!on) MessageBox.Show(this, "Masking is off. Names, addresses, phone numbers and other details will be sent to the cloud AI as they are.", "Cloud AI", MessageBoxButton.OK, MessageBoxImage.Warning); },
            auditLabel: "Cloud AI masking"));
        _providerPanel.Children.Add(Ui.Muted("Replaces names (from your Parties list, and any with Mr, Ms, Dr…), e-mail addresses, phone numbers, postcodes, " +
                                            "National Insurance and case numbers, bank details, dates of birth and street addresses with placeholders like [PERSON-1]. " +
                                            "The answers are put back together on this PC. It's best effort: check the review before sending.", 11.5));
        _providerPanel.Children.Add(Labelled("Documents read at once", SettingCombo("ai.cloud.concurrency", "4",
            new[] { ("1", "1"), ("2", "2"), ("4", "4 (recommended)"), ("6", "6"), ("8", "8") }), "CaseKeeper slows down by itself if the service says it's busy."));
        var reset = Ui.Button("Ask again before sending", null, (_, _) => { AiService.ResetCloudConsent(); _providerStatus.Text = "CaseKeeper will ask again before anything is sent."; },
            tooltip: "Brings back the consent question and the bulk import review for every cloud service");
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 8, 0, 0);
        _providerPanel.Children.Add(reset);
        if (!AiService.HasProviderKey(p.Id)) _providerStatus.Text = "Not connected yet: paste your key and choose Connect.";
    }

    private void SaveCloudChoice(AiProviderInfo p, string model, string effort)
    {
        model = (model ?? "").Trim();
        if (model.Length > 0 && model != AiService.ProviderModel(p.Id))
        {
            Db.SetSetting($"ai.{p.Id}.model", model);
            Db.Audit("settings.change", "settings", null, $"{p.Name} model chosen", model);
        }
        if (effort != AiService.ProviderEffort(p.Id))
        {
            Db.SetSetting($"ai.{p.Id}.effort", effort);
            Db.Audit("settings.change", "settings", null, $"{p.Name} reasoning effort", effort);
        }
    }

    /// <summary>One click: save the key (and resource), check they work, choose the recommended model if none is chosen, and switch AI on.</summary>
    private async Task ConnectCloudAsync(AiProviderInfo p, PasswordBox keyBox, TextBox resourceBox, ComboBox model, ComboBox effort)
    {
        if (keyBox.Password.Length > 0)
        {
            AiService.SetProviderKey(p.Id, keyBox.Password.Trim());
            keyBox.Clear();
            Db.Audit("settings.change", "settings", null, $"{p.Name} key set");
        }
        if (!AiService.HasProviderKey(p.Id)) { _providerStatus.Text = "Paste your key first."; return; }
        if (resourceBox != null)
        {
            var name = AiProviders.ParseResource(resourceBox.Text, out var problem);
            if (name == null) { _providerStatus.Text = problem; return; }
            resourceBox.Text = name;
            if (name != AiService.ProviderResource(p.Id))
            {
                Db.SetSetting($"ai.{p.Id}.resource", name);
                Db.Audit("settings.change", "settings", null, $"{p.Name} resource set", name);
            }
        }
        SaveCloudChoice(p, string.IsNullOrWhiteSpace(model.Text) ? p.DefaultModel : model.Text, p.Efforts[Math.Max(0, effort.SelectedIndex)].Id);
        _providerStatus.Text = "Connecting…";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var config = AiService.CloudConfigFor(p.Id);
            if (!p.IsFoundry)
            {
                // The models this key can use: keep the person's choice if it's there, otherwise the recommended one
                var available = await CloudAi.ListModelsAsync(config, CancellationToken.None);
                if (available.Count > 0 && !available.Contains(config.Model))
                {
                    var pick = available.Contains(p.DefaultModel) ? p.DefaultModel
                        : p.Models.Select(m => m.Id).FirstOrDefault(available.Contains) ?? available[0];
                    SaveCloudChoice(p, pick, config.Effort);
                    model.Text = pick;
                    config = AiService.CloudConfigFor(p.Id);
                }
                model.Items.Clear();
                foreach (var m in available.Count > 0 ? available : p.Models.Select(m => m.Id).ToList()) model.Items.Add(m);
                model.Text = config.Model;
            }
            await CloudAi.TestAsync(config, CancellationToken.None);
            if (Db.GetSetting("ai.enabled", "0") != "1")
            {
                Db.SetSetting("ai.enabled", "1");
                ApplyMode();
            }
            Db.Audit("settings.change", "settings", null, $"AI service connected: {p.Name}", $"Host: {AiProviders.BaseAddress(p.Id, config.Resource).Host}. Model: {config.Model}.");
            AppLog.Info("AI", $"Cloud AI connected ({p.Id})");
            BuildProviderPanel();
            _providerStatus.Text = $"✓ Connected to {p.Name}, using {config.Model} at {config.Effort} effort. AI features are on.";
            _aiView?.Reload();
        }
        catch (Exception ex) when (ex is AiUnavailableException or AiOverloadException or AnalysisValidationException or TimeoutException)
        {
            AppLog.Warn("AI", $"Cloud AI connection test failed ({p.Id}, {ex.GetType().Name})");
            _providerStatus.Text = "Couldn't connect: " + ex.Message;
        }
        finally { Mouse.OverrideCursor = null; }
    }

    // ------------------------------------------------------------------ local AI one-click
    private void BuildLocalSetup()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(Ui.Button("Set up the local AI", "", async (_, _) => await SetUpLocalAiAsync(), primary: true,
            tooltip: $"Checks Ollama is installed and running, downloads CaseKeeper's recommended quick model ({AiHardware.DefaultModel}) and switches AI on"));
        row.Children.Add(Ui.Button(AppPackage.IsPackaged ? "Get Ollama" : "Install Ollama", null, (_, _) => InstallOllama(),
            tooltip: AppPackage.IsPackaged ? "Opens Ollama's download page" : "Installs Ollama with Windows Package Manager (winget)"));
        _providerPanel.Children.Add(row);
    }

    private async Task SetUpLocalAiAsync()
    {
        _providerStatus.Text = "Checking for Ollama…";
        Mouse.OverrideCursor = Cursors.Wait;
        AiService.ServerInfo info;
        try { info = await AiService.ProbeAsync(AiService.Endpoint, AiService.AllowRemote); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Mouse.OverrideCursor = null;
            _providerStatus.Text = "Ollama isn't running on this PC yet. Install it (button on the right), start it, then choose Set up the local AI again.";
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        var model = info.Models.FirstOrDefault(m => AiHardware.Same(m, AiHardware.DefaultModel));
        if (model == null)
        {
            if (MessageBox.Show(this, $"Download CaseKeeper's recommended quick model, {AiHardware.DefaultModel}, into Ollama?\n\nIt's about 3 GB from Ollama's own library " +
                                      "and can take several minutes. Nothing from your cases is sent.", "Local AI",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            {
                model = info.Models.FirstOrDefault();
                if (model == null) { _providerStatus.Text = "No model installed yet."; return; }
            }
            else
            {
                _providerStatus.Text = $"Downloading {AiHardware.DefaultModel}… (this can take several minutes)";
                try { await AiService.PullModelAsync(AiHardware.DefaultModel, CancellationToken.None); model = AiHardware.DefaultModel; }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    AppLog.Warn("AI", $"Model download failed ({ex.GetType().Name})");
                    _providerStatus.Text = "Ollama couldn't download the model. Check this PC is online, then try again.";
                    return;
                }
            }
        }
        if (model != AiService.Model)
        {
            Db.SetSetting("ai.model", model);
            Db.Audit("settings.change", "settings", null, "AI model chosen", model);
            if (_aiModel != null) _aiModel.Text = model;
        }
        if (Db.GetSetting("ai.enabled", "0") != "1") { Db.SetSetting("ai.enabled", "1"); ApplyMode(); }
        _providerStatus.Text = $"✓ The local AI is ready: Ollama {info.Version} with {model}. AI features are on, and nothing leaves this PC.";
        _aiView?.Reload();
    }

    private void InstallOllama()
    {
        const string page = "https://ollama.com/download/windows";
        // Store apps mustn't download or install other programs (policy 10.2.2): the Store copy only opens the page
        if (AppPackage.IsPackaged) { OpenLink(page); return; }
        if (MessageBox.Show(this, "Install Ollama with Windows Package Manager (winget)?\n\nA window opens showing the progress. winget may ask you to accept " +
                                  "its source agreement and Ollama's licence; Windows may ask for permission. When it finishes, start Ollama, then choose " +
                                  "Set up the local AI.", "Install Ollama", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
            if (!File.Exists(winget)) { OpenLink(page); return; }
            var psi = new ProcessStartInfo(winget) { UseShellExecute = true };
            foreach (var a in new[] { "install", "--id", "Ollama.Ollama", "--exact", "--source", "winget" }) psi.ArgumentList.Add(a);
            Process.Start(psi);
            Db.Audit("ai.install", "ai", null, "Started installing Ollama with winget", "");
            _providerStatus.Text = "Installing Ollama in the other window. When it finishes, choose Set up the local AI.";
        }
        catch (Exception ex)
        {
            AppLog.Warn("AI", "Couldn't start winget", ex);
            OpenLink(page);
        }
    }

    private static void OpenLink(string url)
    {
        // Only https pages CaseKeeper itself names: never a link from a document or the AI
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn("Shell", "Couldn't open a web page", ex); }
    }
}
