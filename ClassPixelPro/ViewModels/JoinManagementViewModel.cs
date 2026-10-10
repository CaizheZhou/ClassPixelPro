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
    private string _classIdentity = "";

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

    /// <summary>通过「组织 ID」加入时输入的 5 位短 ID（与配置文件二选一）。</summary>
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

    /// <summary>设备 ID（班级标识），加入集控时必填。</summary>
    public string ClassIdentity
    {
        get => _classIdentity;
        set
        {
            if (value == _classIdentity) return;
            _classIdentity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanJoin));
        }
    }

    /// <summary>设备 ID 已填写，且组织 ID / 配置文件至少提供一种，才允许点「连接」。</summary>
    public bool CanJoin =>
        (!string.IsNullOrWhiteSpace(OrgId) || IsConfigLoaded) &&
        !string.IsNullOrWhiteSpace(ClassIdentity);
}
