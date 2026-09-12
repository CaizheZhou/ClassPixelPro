using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Helpers.UI;
using ClassPixelPro.Core.Models.Automation;
using ClassPixelPro.Services;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Helpers;
using ClassPixelPro.Shared.Models.Profile;
using ClassPixelPro.ViewModels;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Views.WelcomePages;

public partial class RefreshingPage : UserControl, IWelcomePage
{
    public WelcomeViewModel ViewModel { get; set; } = null!;

    private IComponentsService ComponentsService { get; } = IAppHost.GetService<IComponentsService>();
    private IAutomationService AutomationService { get; } = IAppHost.GetService<IAutomationService>();
    private SettingsService SettingsService { get; } = IAppHost.GetService<SettingsService>();
    
    public RefreshingPage()
    {
        InitializeComponent();
    }

    private void ButtonSelectedReservedItems_OnClick(object? sender, RoutedEventArgs e)
    {
        new RefreshingScopesConfigDialog()
        {
            Scopes = ViewModel.RefreshingScopes
        }.ShowDialog((Window)TopLevel.GetTopLevel(this)!);
    }

    private async void ButtonNext_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.IsRefreshingInProgress = true;
        var scope = ViewModel.RefreshingScopes;
        var restartRequired = scope.Plugins || scope.Profile;
        var profile = IAppHost.GetService<IProfileService>().Profile;
        try
        {
            await FileFolderService.CreateBackupAsync();
            if (!scope.Profile)
            {
                if (scope.ProfileClassPlans)
                {
                    foreach (var (key, _) in profile.ClassPlans.Where(x => !scope.ReservedClassPlans.Contains(x.Key))
                                 .ToDictionary())
                    {
                        profile.ClassPlans.Remove(key);
                    }
                }

                var requiredTimeLayouts = profile.ClassPlans
                    .Select(x => x.Value.TimeLayoutId)
                    .ToHashSet();
                if (scope.ProfileTimeLayouts)
                {
                    foreach (var (key, _) in profile.TimeLayouts
                                 .Where(x => !scope.ReservedTimeLayouts.Contains(x.Key) 
                                             && !requiredTimeLayouts.Contains(x.Key))
                                 .ToDictionary())
                    {
                        profile.TimeLayouts.Remove(key);
                    }
                }
            }
            else
            {
                var profileNew = new Profile();
                using var streamReader = new StreamReader(AssetLoader.Open(new Uri("avares://ClassPixelPro/Assets/default-subjects.json",
                    UriKind.Absolute)));
                var subject = await streamReader.ReadToEndAsync();
                profile.Subjects = JsonSerializer.Deserialize<Profile>(subject)!.Subjects;
                var json = JsonSerializer.Serialize(profileNew);
                var newFile = Guid.NewGuid().ToString();
                await File.WriteAllTextAsync(Path.Combine(ProfileService.ProfilePath, $"{newFile}.json"), json);
                SettingsService.Settings.SelectedProfile = $"{newFile}.json";
            }

            if (scope.Components)
            {
                var newName = Guid.NewGuid().ToString();
                var path = Path.Combine(ClassPixelPro.Services.ComponentsService.ComponentSettingsPath,
                    newName + ".json");
                ConfigureFileHelper.SaveConfig(path, ClassPixelPro.Services.ComponentsService.DefaultComponentProfile);
                ComponentsService.RefreshConfigs();
                SettingsService.Settings.CurrentComponentConfig = newName;
            }
            if (scope.Automations)
            {
                var newName = Guid.NewGuid().ToString();
                var path = Path.Combine(Services.AutomationService.AutomationConfigsFolderPath,
                    newName + ".json");
                ConfigureFileHelper.SaveConfig(path, new ObservableCollection<Workflow>());
                AutomationService.RefreshConfigs();
                SettingsService.Settings.CurrentAutomationConfig = newName;
            }

            if (scope.Plugins)
            {
                foreach (var plugin in IPluginService.LoadedPlugins)
                {
                    plugin.IsEnabled = false;
                }
            }
            
            if (!restartRequired)
            {
                this.ShowToast("翻新成功。");
                WelcomeWindow.WelcomeNavigateForwardCommand.Execute(this);
                return;
            }

            List<string> args = ["--refreshing", "-m"];
            if (ViewModel.IsOnboarding)
            {
                args.Add("--onboarding");
            }

            ViewModel.IsManuallyRestarted = true;
            AppBase.Current.Restart(args.ToArray());
        }
        catch (Exception exception)
        {
            this.ShowErrorToast("无法翻新设置", exception);
            IAppHost.GetService<ILogger<RefreshingPage>>().LogError(exception, "无法翻新设置");
        }
        finally
        {
            if (!restartRequired)
            {
                ViewModel.IsRefreshingInProgress = false;
            }
        }
    }
}