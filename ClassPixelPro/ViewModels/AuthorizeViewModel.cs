using System.Collections.ObjectModel;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Models.Authorize;
using CommunityToolkit.Mvvm.ComponentModel;
using AuthorizeProviderDisplayingModel = ClassPixelPro.Models.Authorize.AuthorizeProviderDisplayingModel;

namespace ClassPixelPro.ViewModels;

public partial class AuthorizeViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<AuthorizeProviderDisplayingModel> _providers = [];

    [ObservableProperty] private bool _isEditingMode = false;

    [ObservableProperty] private Credential _credential = new();

    [ObservableProperty] private AuthorizeProviderInfo? _selectedAuthorizeProviderInfo;

    [ObservableProperty] private CredentialItem? _selectedCredentialItem;
}