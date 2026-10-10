using ClassPixelPro.Shared.Models.Management;

using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.ViewModels;

public class JoinManagementViewModel : ObservableRecipient
{
    private ManagementSettings _managementSettings = new();
    private string _errorMessage = "";
    private bool _isErrorMessageOpen = false;
    private bool _isWorking = false;
    private string _orgId = "";

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

    /// <summary>通过「组织 ID」加入时输入的 5 位短 ID（必填）。</summary>
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

    /// <summary>组织 ID 非空时才允许点「连接」。</summary>
    public bool CanJoin => !string.IsNullOrWhiteSpace(OrgId);
}
