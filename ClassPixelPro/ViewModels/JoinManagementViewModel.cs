using ClassPixelPro.Shared.Models.Management;

using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.ViewModels;

public class JoinManagementViewModel : ObservableRecipient
{
    private string _configFilePath = "";
    private ManagementSettings _managementSettings = new();
    private string _errorMessage = "";
    private bool _isConfigLoaded = false;
    private bool _isErrorMessageOpen = false;
    private bool _isWorking = false;
    private string _orgId = "";
    private string _serverBaseUrl = "https://class.imzcz.cn";

    public string ConfigFilePath
    {
        get => _configFilePath;
        set
        {
            if (value == _configFilePath) return;
            _configFilePath = value;
            OnPropertyChanged();
        }
    }

    public ManagementSettings ManagementSettings
    {
        get => _managementSettings;
        set
        {
            if (Equals(value, _managementSettings)) return;
            _managementSettings = value;
            OnPropertyChanged();
        }
    }

    public bool IsConfigLoaded
    {
        get => _isConfigLoaded;
        set
        {
            if (value == _isConfigLoaded) return;
            _isConfigLoaded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanJoin));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (value == _errorMessage) return;
            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsErrorMessageOpen
    {
        get => _isErrorMessageOpen;
        set
        {
            if (value == _isErrorMessageOpen) return;
            _isErrorMessageOpen = value;
            OnPropertyChanged();
        }
    }

    public bool IsWorking
    {
        get => _isWorking;
        set
        {
            if (value == _isWorking) return;
            _isWorking = value;
            OnPropertyChanged();
        }
    }

    /// <summary>通过「组织 ID」加入时输入的 5 位短 ID。</summary>
    public string OrgId
    {
        get => _orgId;
        set
        {
            if (value == _orgId) return;
            _orgId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanJoin));
        }
    }

    /// <summary>集控服务器地址（默认官方地址）。</summary>
    public string ServerBaseUrl
    {
        get => _serverBaseUrl;
        set
        {
            if (value == _serverBaseUrl) return;
            _serverBaseUrl = value;
            OnPropertyChanged();
        }
    }

    /// <summary>有组织 ID 或已加载配置文件时，允许点「连接」。</summary>
    public bool CanJoin => !string.IsNullOrWhiteSpace(OrgId) || IsConfigLoaded;
}