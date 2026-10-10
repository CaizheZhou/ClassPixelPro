using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassPixelPro.Core;
using FluentAvalonia.UI.Controls;
using Path = System.IO.Path;

namespace ClassPixelPro.Helpers;

/// <summary>
/// 管理磁盘上的版本目录（<c>app-&lt;版本&gt;-&lt;编号&gt;</c>），供「回滚」与版本清理使用。
/// 标记约定：
/// <list type="bullet">
/// <item><description><c>.current</c>：当前应启动的版本，启动器优先选择它。</description></item>
/// <item><description><c>.prev</c>：保留下来可回滚的历史版本。</description></item>
/// <item><description><c>.destroy</c>：待删除，由下次启动时的清理逻辑删除。</description></item>
/// <item><description><c>.partial</c>：部署未完成，不参与回滚。</description></item>
/// </list>
/// </summary>
public static class DeploymentManager
{
    /// <summary>
    /// 保留的历史版本数量上限。每个版本都是一份完整的应用目录（数百 MB），
    /// 部署新版本时若超出上限就删除最旧的，避免磁盘无限增长。
    /// </summary>
    public const int MaxRetainedPreviousVersions = 2;

    public sealed record Deployment(string DirectoryPath, string Name, bool IsCurrent, bool IsRollbackTarget, ulong SizeBytes);

    private static string PackageRoot => CommonDirectories.AppPackageRoot;

    private static string CurrentDirectory => Path.GetFullPath(Environment.CurrentDirectory);

    private static bool IsValidVersionDirectory(string dir) =>
        File.Exists(Path.Combine(dir, "ClassPixelPro.Desktop" + AppBase.PlatformExecutableExtension));

    /// <summary>列出磁盘上可用的版本目录，当前版本排在最前。</summary>
    public static List<Deployment> GetDeployments(bool withSize = false)
    {
        var current = CurrentDirectory;
        return Directory.GetDirectories(PackageRoot)
            .Where(dir => Path.GetFileName(dir).StartsWith("app", StringComparison.OrdinalIgnoreCase) &&
                          !File.Exists(Path.Combine(dir, ".partial")) &&
                          IsValidVersionDirectory(dir))
            .Select(dir => new Deployment(
                Path.GetFullPath(dir),
                Path.GetFileName(dir),
                Path.GetFullPath(dir) == current,
                File.Exists(Path.Combine(dir, ".prev")),
                withSize ? StorageSizeHelper.GetFolderStorageSize(dir) : 0))
            .OrderByDescending(x => x.IsCurrent)
            .ThenByDescending(x => ParseVersion(x.Name))
            .ToList();
    }

    /// <summary>
    /// 部署新版本时把当前版本标记为可回滚的 <c>.prev</c>，并把超出保留上限的最旧版本标记为待删除。
    /// </summary>
    public static void MarkCurrentAsPrevious()
    {
        var current = CurrentDirectory;
        try
        {
            // 当前版本即将成为新的 .prev，所以现有 .prev 只能再保留 MaxRetainedPreviousVersions - 1 个
            var stale = Directory.GetDirectories(PackageRoot)
                .Where(dir => Path.GetFullPath(dir) != current &&
                              File.Exists(Path.Combine(dir, ".prev")))
                .OrderByDescending(dir => ParseVersion(Path.GetFileName(dir)))
                .Skip(MaxRetainedPreviousVersions - 1)
                .ToList();
            foreach (var dir in stale)
            {
                File.Delete(Path.Combine(dir, ".prev"));
                File.WriteAllText(Path.Combine(dir, ".destroy"), "");
            }
        }
        catch (Exception e)
        {
            // 清理失败不阻断部署，残留目录留给后续启动时的清理逻辑
            Console.WriteLine(e);
        }

        // 让位给新版本：摘掉 .current，标记为可回滚的 .prev
        File.Delete(Path.Combine(current, ".current"));
        File.WriteAllText(Path.Combine(current, ".prev"), "");
    }

    /// <summary>把指定版本切换为当前版本；切换前的当前版本保留为可回滚的 <c>.prev</c>。</summary>
    public static void RollbackTo(string directoryPath)
    {
        var target = Path.GetFullPath(directoryPath);
        var current = CurrentDirectory;
        File.Delete(Path.Combine(target, ".prev"));
        File.WriteAllText(Path.Combine(target, ".current"), "");
        if (target == current)
        {
            return;
        }

        File.Delete(Path.Combine(current, ".current"));
        File.WriteAllText(Path.Combine(current, ".prev"), "");
    }

    /// <summary>删除指定版本目录；文件被占用时退回标记删除，交由下次启动清理。</summary>
    public static void DeleteDeployment(string directoryPath)
    {
        var target = Path.GetFullPath(directoryPath);
        if (target == CurrentDirectory)
        {
            throw new InvalidOperationException("不能删除当前正在运行的版本。");
        }

        try
        {
            Directory.Delete(target, true);
        }
        catch (Exception)
        {
            File.WriteAllText(Path.Combine(target, ".destroy"), "");
            throw;
        }
    }

    /// <summary>打开版本管理对话框：可回滚到某个历史版本，或删除旧版本释放磁盘空间。</summary>
    public static async Task ShowVersionManagerAsync(TopLevel? root)
    {
        // 计算目录占用要遍历所有文件，放到后台线程避免卡住界面
        var deployments = await Task.Run(() => GetDeployments(withSize: true));
        Deployment? rollbackTarget = null;
        Deployment? deleteTarget = null;

        var dialog = new ContentDialog
        {
            Title = "版本管理",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 480 };
        panel.Children.Add(new TextBlock
        {
            Text = $"保留最近 {MaxRetainedPreviousVersions} 个历史版本用于回滚。每个版本都是一份完整的应用目录，删除后可释放磁盘空间。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        });
        foreach (var deployment in deployments)
        {
            panel.Children.Add(BuildDeploymentRow(deployment, dialog, x => rollbackTarget = x, x => deleteTarget = x));
        }

        dialog.Content = panel;
        await dialog.ShowAsync(root);

        if (rollbackTarget != null)
        {
            RollbackTo(rollbackTarget.DirectoryPath);
            AppBase.Current.Restart([], true);
            return;
        }

        if (deleteTarget != null)
        {
            DeleteDeployment(deleteTarget.DirectoryPath);
            // 重新打开以刷新列表，让用户看到释放后的结果
            await ShowVersionManagerAsync(root);
        }
    }

    private static Control BuildDeploymentRow(Deployment deployment, ContentDialog dialog,
        Action<Deployment> onRollback, Action<Deployment> onDelete)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto") };
        row.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = deployment.Name, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = StorageSizeHelper.FormatSize(deployment.SizeBytes) +
                           (deployment.IsRollbackTarget ? " · 可回滚" : ""),
                    Opacity = 0.7,
                    FontSize = 12
                }
            }
        });

        if (deployment.IsCurrent)
        {
            var badge = new TextBlock
            {
                Text = "当前版本",
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(badge, 1);
            row.Children.Add(badge);
            return row;
        }

        var rollbackButton = new Button { Content = "回滚", Margin = new Thickness(8, 0, 0, 0) };
        rollbackButton.Click += (_, _) =>
        {
            onRollback(deployment);
            dialog.Hide();
        };
        Grid.SetColumn(rollbackButton, 1);
        row.Children.Add(rollbackButton);

        var deleteButton = new Button
        {
            Content = "删除",
            Classes = { "danger" },
            Margin = new Thickness(8, 0, 0, 0)
        };
        deleteButton.Click += (_, _) =>
        {
            onDelete(deployment);
            dialog.Hide();
        };
        Grid.SetColumn(deleteButton, 2);
        row.Children.Add(deleteButton);

        return row;
    }

    private static Version ParseVersion(string directoryName)
    {
        var split = directoryName.Split('-');
        return split.Length > 1 && Version.TryParse(split[1], out var version) ? version : new Version();
    }
}
