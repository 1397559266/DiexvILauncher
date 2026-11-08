using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using ILauncher.Views;

namespace ILauncher.ViewModels;

public sealed class ServersViewModel : ObservableObject
{
    private readonly GameInstance _instance;
    private readonly ServersService _service = new();
    private readonly Action<string> _join;
    private readonly Action<string, bool> _toast;
    private bool _isLoading;

    public ServersViewModel(GameInstance instance, Action<string> join, Action<string, bool> toast)
    {
        _instance = instance;
        _join = join;
        _toast = toast;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        JoinCommand = new RelayCommand(parameter =>
        {
            if (parameter is ServerEntry server)
            {
                _join(server.Ip);
            }
        });
        AddCommand = new RelayCommand(AddServer);
        EditCommand = new RelayCommand(parameter =>
        {
            if (parameter is ServerEntry server)
            {
                EditServer(server);
            }
        });
        DeleteCommand = new RelayCommand(parameter =>
        {
            if (parameter is ServerEntry server)
            {
                DeleteServer(server);
            }
        });
        OpenFolderCommand = new RelayCommand(OpenFolder);
        _ = LoadAsync();
    }

    public ObservableCollection<ServerEntry> Servers { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand JoinCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsEmpty => Servers.Count == 0;
    public string CountText => $"{Servers.Count} 个服务器";
    public string FilePath => Path.Combine(_instance.GameDirectory, "servers.dat");

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var servers = await Task.Run(() => _service.Load(_instance.GameDirectory));
            Servers.Clear();
            foreach (var server in servers)
            {
                Servers.Add(server);
            }

            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CountText));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OpenFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_instance.GameDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast($"无法打开目录：{ex.Message}", true);
        }
    }

    private void AddServer()
    {
        var dialog = new ServerEditWindow { Owner = Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Servers.Add(new ServerEntry(dialog.ServerName, dialog.ServerAddress, null));
        Save();
    }

    private void EditServer(ServerEntry server)
    {
        var index = Servers.IndexOf(server);
        if (index < 0)
        {
            return;
        }

        var dialog = new ServerEditWindow(server) { Owner = Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Servers[index] = new ServerEntry(dialog.ServerName, dialog.ServerAddress, server.IconBase64);
        Save();
    }

    private void DeleteServer(ServerEntry server)
    {
        if (MessageBox.Show($"确定要删除服务器「{server.Name}」吗？", "删除服务器",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        Servers.Remove(server);
        Save();
    }

    private void Save()
    {
        try
        {
            _service.Save(_instance.GameDirectory, Servers);
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CountText));
            _toast("服务器列表已保存。", false);
        }
        catch (Exception ex)
        {
            _toast($"保存服务器列表失败：{ex.Message}", true);
        }
    }
}
