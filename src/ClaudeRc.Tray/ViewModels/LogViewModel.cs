using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeRc.Tray.ViewModels;

/// <summary>The folder log viewer (row menu → View log): the last lines of the folder log (SPEC 11), from the host's <c>log</c> op.</summary>
internal sealed class LogViewModel : ObservableObject
{
    /// <summary>How many lines are asked for.</summary>
    public const int LineCount = 500;

    private readonly HostConnection _connection;
    private readonly string _path;
    private string _text = "";

    /// <summary>Creates the view-model for one folder.</summary>
    public LogViewModel(HostConnection connection, string path, string name)
    {
        _connection = connection;
        _path = path;
        Title = "Log: " + name;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
    }

    /// <summary>Window title.</summary>
    public string Title { get; }

    /// <summary>The folder's path.</summary>
    public string Path => _path;

    /// <summary>The log lines, or why they could not be read.</summary>
    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    /// <summary>Reads the log again.</summary>
    public IAsyncRelayCommand RefreshCommand { get; }

    /// <summary>Reads the log.</summary>
    public async Task LoadAsync()
    {
        var result = await _connection.SendAsync(new LogRequest(_path, LineCount));
        Text = result.Ok ? string.Join("\n", result.Response?.Lines ?? []) : result.Error ?? "";
    }
}
