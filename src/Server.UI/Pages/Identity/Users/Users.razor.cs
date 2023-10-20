using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Claims;
using AutoMapper;
using BlazorDownloadFile;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Extensions;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.MultiTenant;
using CleanArchitecture.Blazor.Application.Features.Identity.Dto;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Constants.ClaimTypes;
using CleanArchitecture.Blazor.Infrastructure.Constants.Role;
using CleanArchitecture.Blazor.Server.UI.Components.Common;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Roles;
using LazyCache;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Identity;
using MudExtensions;

namespace CleanArchitecture.Blazor.Server.UI.Pages.Identity.Users;
public partial class Users
{
    [Inject]
    private IUsersStateContainer UsersStateContainer { get; set; } = default!;
    private int _defaultPageSize = 15;
    private HashSet<ApplicationUserDto> _selectedItems = new();
    private readonly ApplicationUserDto _currentDto = new();
    private string _searchString = string.Empty;
    private string Title { get; set; } = "Users";
    private List<PermissionModel> _permissions = new();
    private IList<Claim> _assignedClaims = default!;
    [CascadingParameter]
    private Task<AuthenticationState> AuthState { get; set; } = default!;
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    private RoleManager<ApplicationRole> RoleManager { get; set; } = default!;
    private TimeSpan RefreshInterval => TimeSpan.FromSeconds(60);
    private LazyCacheEntryOptions Options => new LazyCacheEntryOptions().SetAbsoluteExpiration(RefreshInterval, ExpirationMode.LazyExpiration);
    [Inject]
    private IAppCache Cache { get; set; } = null!;
    [Inject]
    private ITenantProvider TenantProvider { get; set; } = default!;
    [Inject]
    private ITenantService TenantsService { get; set; } = null!;
    [Inject]
    private IUserDataProvider UserDataProvider { get; set; } = null!;
    [Inject]
    private IBlazorDownloadFileService BlazorDownloadFileService { get; set; } = null!;
    [Inject]
    private IExcelService ExcelService { get; set; } = null!;
    [Inject]
    private IMapper Mapper { get; set; } = null!;
    private MudDataGrid<ApplicationUserDto> _table = null!;
    private bool _processing;
    private bool _showPermissionsDrawer;
    private bool _canCreate;
    private bool _canSearch;
    private bool _canEdit;
    private bool _canDelete;
    private bool _canActive;
    private bool _canManageRoles;
    private bool _canRestPassword;
    private bool _canManagePermissions;
    private bool _canImport;
    private bool _canExport;
    private bool _loading;
    private bool _exporting;
    private bool _uploading;
    private List<string?> _roles = new();
    private string? _searchRole;

    protected override async Task OnInitializedAsync()
    {

        UserManager = ScopedServices.GetRequiredService<UserManager<ApplicationUser>>();
        RoleManager = ScopedServices.GetRequiredService<RoleManager<ApplicationRole>>();
        Title = L[_currentDto.GetClassDescription()];
        var state = await AuthState;
        _canCreate = (await AuthService.AuthorizeAsync(state.User, Permissions.Users.Create)).Succeeded;
        _canSearch = (await AuthService.AuthorizeAsync(state.User, Permissions.Users.Search)).Succeeded;
        _canEdit = (await AuthService.AuthorizeAsync(state.User, Permissions.Users.Edit)).Succeeded;
        _canDelete = (await AuthService.AuthorizeAsync(state.User, Permissions.Users.Delete)).Succeeded;
        _canActive = (await AuthService.AuthorizeAsync(state.User, Permissions.Users.Active)).Succeeded;
        _canManageRoles = (await AuthService.AuthorizeAsync(state.User, Permissions.Users.ManageRoles)).Succeeded;
        _canRestPassword = (await AuthService.AuthorizeAsync(state.User, Permissions.Users.RestPassword)).Succeeded;
        _canManagePermissions = (await AuthService.AuthorizeAsync(state.User, Permissions.Users.ManagePermissions)).Succeeded;
        _canImport = (await AuthService.AuthorizeAsync(state.User, Permissions.Users.Import)).Succeeded;
        _canExport = (await AuthService.AuthorizeAsync(state.User, Permissions.Users.Export)).Succeeded;
        _roles = await RoleManager.Roles.Select(x => x.Name).ToListAsync();
    }
    private bool isOnline(string username)
    {
        return UsersStateContainer.UsersByConnectionId.Any(x => x.Value.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<GridData<ApplicationUserDto>> ServerReload(GridState<ApplicationUserDto> state)
    {
        try
        {
            _loading = true;
            Expression<Func<ApplicationUser, bool>> searchPredicate = x =>
            x.UserName!.ToLower().Contains(_searchString) ||
            x.Email!.ToLower().Contains(_searchString) ||
            x.DisplayName!.ToLower().Contains(_searchString) ||
            x.PhoneNumber!.ToLower().Contains(_searchString) ||
            x.TenantName!.ToLower().Contains(_searchString) ||
            x.Provider!.ToLower().Contains(_searchString);
            var query = UserManager.Users.Where(searchPredicate);
            if (!string.IsNullOrEmpty(_searchRole))
            {
                query = query.Where(x => x.UserRoles.Any(y => y.Role.Name == _searchRole));
            }
            var items = await query
                 .Include(x => x.UserRoles)
                 .Include(x => x.Superior)
                 .EfOrderBySortDefinitions(state)
                 .Skip(state.Page * state.PageSize).Take(state.PageSize).ProjectTo<ApplicationUserDto>(Mapper.ConfigurationProvider).ToListAsync();
            var total = await UserManager.Users.CountAsync(searchPredicate);
            return new GridData<ApplicationUserDto> { TotalItems = total, Items = items };
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnSearch(string text)
    {
        if (_loading) return;
        _searchString = text.ToLower();
        await _table.ReloadServerData();
    }

    private async Task OnSearchRole(string role)
    {
        if (_loading) return;
        _searchRole = role;
        await _table.ReloadServerData();
    }
    private async Task OnRefresh()
    {
        await _table.ReloadServerData();
    }
    private async Task OnCreate()
    {
        var model = new ApplicationUserDto { Provider = "Local", Email = "", UserName = "", AssignedRoles = new[] { RoleName.Basic } };
        var parameters = new DialogParameters<_UserFormDialog> {
        { x=>x.Model,model }
       };
        var options = new DialogOptions { CloseButton = true, CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true };
        var dialog = DialogService.Show<_UserFormDialog>(L["Create a new user"], parameters, options);
        var result = await dialog.Result;
        if (!result.Canceled)
        {
            var applicationUser = new ApplicationUser
            {
                Provider = model.Provider,
                DisplayName = model.DisplayName,
                UserName = model.UserName,
                TenantId = model.TenantId,
                TenantName = model.TenantName,
                Email = model.Email,
                PhoneNumber = model.PhoneNumber,
                SuperiorId = model.SuperiorId,
                ProfilePictureDataUrl = model.ProfilePictureDataUrl,
                IsActive = model.IsActive,
            };
            var password = model.Password;
            var state = await UserManager.CreateAsync(applicationUser, password!);

            if (state.Succeeded)
            {
                if (model.AssignedRoles is not null && model.AssignedRoles.Length > 0)
                {
                    await UserManager.AddToRolesAsync(applicationUser, model.AssignedRoles);
                }
                else
                {
                    await UserManager.AddToRoleAsync(applicationUser, RoleName.Basic);
                }
                Snackbar.Add($"{ConstantString.CreateSuccess}", Severity.Info);
                await UserDataProvider.Refresh();
                await OnRefresh();
            }
            else
            {
                Snackbar.Add($"{string.Join(",", state.Errors.Select(x => x.Description).ToArray())}", Severity.Error);
            }
        }
    }

    private async Task OnEdit(ApplicationUserDto item)
    {

        var parameters = new DialogParameters<_UserFormDialog> {
        { x=>x.Model,item }
       };
        var options = new DialogOptions { CloseButton = true, CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true };
        var dialog = DialogService.Show<_UserFormDialog>(L["Edit the user"], parameters, options);
        var result = await dialog.Result;
        if (!result.Canceled)
        {
            var user = await UserManager.FindByIdAsync(item.Id!) ?? throw new NotFoundException($"The application user [{item.Id}] was not found.");
            var state = await AuthState;
            user.Email = item.Email;
            user.PhoneNumber = item.PhoneNumber;
            user.ProfilePictureDataUrl = item.ProfilePictureDataUrl;
            user.DisplayName = item.DisplayName;
            user.Provider = item.Provider;
            user.UserName = item.UserName;
            user.IsActive = item.IsActive;
            user.TenantId = item.TenantId;
            user.TenantName = item.TenantName;
            user.SuperiorId = item.SuperiorId;
            var identityResult = await UserManager.UpdateAsync(user);
            if (identityResult.Succeeded)
            {
                var roles = await UserManager.GetRolesAsync(user!);
                if (roles.Count > 0)
                {
                    var removeRoleResult = await UserManager.RemoveFromRolesAsync(user, roles);
                    if (removeRoleResult.Succeeded)
                    {
                        if (item.AssignedRoles is not null && item.AssignedRoles.Length > 0)
                        {
                            await UserManager.AddToRolesAsync(user, item.AssignedRoles);
                        }
                    }
                }
                Snackbar.Add($"{ConstantString.UpdateSuccess}", Severity.Info);
                await OnRefresh();
                await UserDataProvider.Refresh();
            }
            else
            {
                Snackbar.Add($"{string.Join(",", identityResult.Errors.Select(x => x.Description).ToArray())}", Severity.Error);
            }
        }
    }

    private async Task OnDelete(ApplicationUserDto dto)
    {
        var deleteContent = ConstantString.DeleteConfirmation;
        var parameters = new DialogParameters<ConfirmationDialog>
    {
        { x=>x.ContentText, string.Format(deleteContent, dto.UserName) }
    };
        var options = new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.ExtraSmall, FullWidth = true, DisableBackdropClick = true };
        var dialog = DialogService.Show<ConfirmationDialog>(ConstantString.DeleteConfirmationTitle, parameters, options);
        var result = await dialog.Result;
        if (!result.Canceled)
        {
            // Requesting the current user id
            var state = await AuthState;
            var currentUserId = state.User.GetUserId();

            // Checks if the current user is trying to remove his own account
            if (currentUserId != null && currentUserId.Equals(dto.Id))
            {
                Snackbar.Add("You cannot delete your own account!", Severity.Error);
                return;
            }

            // Trying to find the user that needs to be deleted in the database (nullable check)
            var requestedDeletedUser = await UserManager.FindByIdAsync(dto.Id);
            if (requestedDeletedUser == null)
            {
                Snackbar.Add("The user doesn't seem to exist in the database!", Severity.Error);
                return;
            }

            // Trying to remove the requested user
            var deleteResult = await UserManager.DeleteAsync(requestedDeletedUser);
            if (!deleteResult.Succeeded)
            {
                Snackbar.Add($"{string.Join(",", deleteResult.Errors.Select(x => x.Description).ToArray())}", Severity.Error);
                return;
            }

            Snackbar.Add($"{ConstantString.DeleteSuccess}", Severity.Info);
            await OnRefresh();
            await UserDataProvider.Refresh();
        }
    }

    private async Task OnDeleteChecked()
    {
        var state = await AuthState;
        var currentUserId = state.User.GetUserId();
        var isSelectedItemContainCurrentUser = _selectedItems.Any(x => x.Id == currentUserId);

        if (isSelectedItemContainCurrentUser)
        {
            if (_selectedItems.Count == 1)
            {
                Snackbar.Add("You cannot delete your own account!", Severity.Error);
                return;
            }
            _selectedItems.Remove(_selectedItems.First(x => x.Id == currentUserId));
        }

        string deleteContent = ConstantString.DeleteConfirmation;
        var parameters = new DialogParameters<ConfirmationDialog> {
        {
            x=>x.ContentText, string.Format(deleteContent, _selectedItems.Count) }
       };
        var options = new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.ExtraSmall, FullWidth = true, DisableBackdropClick = true };
        var dialog = DialogService.Show<ConfirmationDialog>(L["Delete"], parameters, options);
        var result = await dialog.Result;

        if (!result.Canceled)
        {
            var deleteId = _selectedItems.Select(x => x.Id).ToArray();
            var deleteUsers = await UserManager.Users.Where(x => deleteId.Contains(x.Id)).ToListAsync();

            foreach (var deleteUser in deleteUsers)
            {
                var deleteResult = await UserManager.DeleteAsync(deleteUser);
                if (!deleteResult.Succeeded)
                {
                    Snackbar.Add($"{string.Join(",", deleteResult.Errors.Select(x => x.Description).ToArray())}", Severity.Error);
                    return;
                }
            }
            Snackbar.Add($"{ConstantString.DeleteSuccess}", Severity.Info);
            await OnRefresh();
            await UserDataProvider.Refresh();
        }

    }

    private async Task OnSetActive(ApplicationUserDto item)
    {
        var user = await UserManager.FindByIdAsync(item.Id!) ?? throw new NotFoundException($"Application user not found {item.Id}.");
        user.IsActive = !item.IsActive;
        var state = await UserManager.UpdateAsync(user);
        item.IsActive = !item.IsActive;
        if (state.Succeeded)
        {
            Snackbar.Add($"{ConstantString.UpdateSuccess}", Severity.Info);
        }
        else
        {
            Snackbar.Add($"{string.Join(",", state.Errors.Select(x => x.Description).ToArray())}", Severity.Error);
        }
    }

    private async Task OnResetPassword(ApplicationUserDto item)
    {

        var model = new ResetPasswordFormModel { Id = item.Id, DisplayName = item.DisplayName, UserName = item.UserName, ProfilePictureDataUrl = item.ProfilePictureDataUrl };
        var parameters = new DialogParameters<_ResetPasswordDialog> {
        {x=>x.Model, model }
    };
        var options = new DialogOptions { CloseOnEscapeKey = true, CloseButton = true, MaxWidth = MaxWidth.ExtraSmall };
        var dialog = DialogService.Show<_ResetPasswordDialog>(L["Set Password"], parameters, options);
        var result = await dialog.Result;
        if (!result.Canceled)
        {

            var user = await UserManager.FindByIdAsync(item.Id!);
            var token = await UserManager.GeneratePasswordResetTokenAsync(user!);
            var state = await UserManager.ResetPasswordAsync(user!, token, model!.Password!);
            if (state.Succeeded)
            {
                Snackbar.Add($"{L["Reset password successfully"]}", Severity.Info);
            }
            else
            {
                Snackbar.Add($"{string.Join(",", state.Errors.Select(x => x.Description).ToArray())}", Severity.Error);
            }
        }
    }
    private async Task OnSetPermissions(ApplicationUserDto item)
    {
        _showPermissionsDrawer = true;
        _permissions = new();
        _permissions = await GetAllPermissions(item);


    }
    private Task OnOpenChangedHandler(bool state)
    {
        _showPermissionsDrawer = state;
        return Task.CompletedTask;
    }
    private async Task<List<PermissionModel>> GetAllPermissions(ApplicationUserDto dto)
    {
        async Task<IList<Claim>> GetClaims(string userId)
        {
            var user = await UserManager.FindByIdAsync(dto.Id) ?? throw new NotFoundException($"not found application user: {userId}");
            var claims = await UserManager.GetClaimsAsync(user);
            return claims;
        }

        var key = $"get-claims-by-{dto.Id}";
        _assignedClaims = await Cache.GetOrAddAsync(key, async () => await GetClaims(dto.Id), Options);
        var allPermissions = new List<PermissionModel>();
        var modules = typeof(Permissions).GetNestedTypes();
        foreach (var module in modules)
        {
            var moduleName = string.Empty;
            var moduleDescription = string.Empty;
            if (module.GetCustomAttributes(typeof(DisplayNameAttribute), true)
                .FirstOrDefault() is DisplayNameAttribute displayNameAttribute)
                moduleName = displayNameAttribute.DisplayName;

            if (module.GetCustomAttributes(typeof(DescriptionAttribute), true)
                .FirstOrDefault() is DescriptionAttribute descriptionAttribute)
                moduleDescription = descriptionAttribute.Description;

            var fields = module.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            allPermissions.AddRange(from field in fields
                                    select field.GetValue(null)
                into propertyValue
                                    where propertyValue is not null
                                    select propertyValue.ToString()
                into claimValue
                                    select new PermissionModel
                                    {
                                        UserId = dto.Id,
                                        ClaimValue = claimValue,
                                        ClaimType = ApplicationClaimTypes.Permission,
                                        Group = moduleName,
                                        Description = moduleDescription,
                                        Assigned = _assignedClaims.Any(x => x.Value == claimValue)
                                    });
        }
        return allPermissions;
    }
    private async Task OnAssignAllChangedHandler(List<PermissionModel> models)
    {
        try
        {
            _processing = true;
            var userId = models.First().UserId;
            var user = await UserManager.FindByIdAsync(userId!) ?? throw new NotFoundException($"not found application user: {userId}");
            foreach (var model in models)
            {
                if (model.Assigned)
                {
                    if (model.ClaimType is not null && model.ClaimValue is not null)
                    {
                        await UserManager.AddClaimAsync(user, new Claim(model.ClaimType, model.ClaimValue));
                    }
                }
                else
                {
                    var removed = _assignedClaims.FirstOrDefault(x => x.Value == model.ClaimValue);
                    if (removed is not null)
                    {
                        await UserManager.RemoveClaimAsync(user, removed);
                    }
                }
            }

            Snackbar.Add($"{L["Authorization has been changed"]}", Severity.Info);
            await Task.Delay(300);
            var key = $"get-claims-by-{user.Id}";
            Cache.Remove(key);
        }
        finally
        {
            _processing = false;
        }
    }

    private async Task OnAssignChangedHandler(PermissionModel model)
    {
        try
        {
            _processing = true;
            var userId = model.UserId!;
            var user = await UserManager.FindByIdAsync(userId) ?? throw new NotFoundException($"Application user Not Found {userId}."); ;
            model.Assigned = !model.Assigned;
            if (model is { Assigned: true, ClaimType: not null, ClaimValue: not null })
            {
                await UserManager.AddClaimAsync(user, new Claim(model.ClaimType, model.ClaimValue));
                Snackbar.Add($"{L["Permission added successfully!"]}", Severity.Info);
            }
            else
            {
                var removed = _assignedClaims.FirstOrDefault(x => x.Value == model.ClaimValue);
                if (removed is not null)
                {
                    await UserManager.RemoveClaimAsync(user, removed);
                }
                Snackbar.Add($"{L["Permission removed successfully!"]}", Severity.Info);
            }
            var key = $"get-claims-by-{user.Id}";
            Cache.Remove(key);

        }
        finally
        {
            _processing = false;
        }

    }
    private async Task OnExport()
    {
        try
        {
            _exporting = true;
            Expression<Func<ApplicationUser, bool>> searchPredicate = x =>
            (x.UserName!.Contains(_searchString) ||
            x.Email!.Contains(_searchString) ||
            x.DisplayName!.Contains(_searchString) ||
            x.PhoneNumber!.Contains(_searchString) ||
            x.TenantName!.Contains(_searchString) ||
            x.Provider!.Contains(_searchString)) &&
            (_searchRole == null || (_searchRole != null && x.UserRoles.Any(x => x.Role.Name == _searchRole)));
            var items = await UserManager.Users.Where(searchPredicate)
                 .Select(x => new ApplicationUserDto
                 {
                     Id = x.Id,
                     UserName = x.UserName!,
                     DisplayName = x.DisplayName,
                     Email = x.Email!,
                     PhoneNumber = x.PhoneNumber,
                     TenantId = x.TenantId,
                     TenantName = x.TenantName,
                 }).ToListAsync();
            var result = await ExcelService.ExportAsync(items,
                    new Dictionary<string, Func<ApplicationUserDto, object?>>
                                    {
                  {L["Id"],item => item.Id},
                  {L["User Name"],item => item.UserName},
                  {L["Display Name"],item => item.DisplayName},
                  {L["Email"],item => item.Email},
                  {L["Phone Number"],item => item.PhoneNumber},
                  {L["Tenant Id"],item => item.TenantId},
                  {L["Tenant Name"],item => item.TenantName},
                                                                            }
                    , L["Users"]);
            var downloadResult = await BlazorDownloadFileService.DownloadFile($"{L["Users"]}.xlsx", result, contentType: "application/octet-stream");
            Snackbar.Add($"{ConstantString.ExportSuccess}", Severity.Info);
        }
        finally
        {
            _exporting = false;
        }

    }

    private async Task OnImportData(IBrowserFile file)
    {
        _uploading = true;
        var stream = new MemoryStream();
        await file.OpenReadStream(GlobalVariable.MaxAllowedSize).CopyToAsync(stream);
        var result = await ExcelService.ImportAsync(stream.ToArray(), mappers: new Dictionary<string, Func<DataRow, ApplicationUser, object?>>
        {
            { L["User Name"], (row, item) => item.UserName = row[L["User Name"]]?.ToString() },
            { L["Display Name"], (row, item) => item.DisplayName = row[L["Display Name"]]?.ToString() },
            { L["Email"], (row, item) => item.Email = row[L["Email"]]?.ToString() },
            { L["Phone Number"], (row, item) => item.PhoneNumber = row[L["Phone Number"]]?.ToString() },
            { L["Tenant Name"], (row, item) => item.TenantName = row[L["Tenant Name"]]?.ToString() },
            { L["Tenant Id"], (row, item) => item.TenantId = row[L["Tenant Id"]]?.ToString() },
        }, L["Users"]);
        if (result.Succeeded)
        {
            foreach (var user in result.Data!)
            {
                if (!UserManager.Users.Any(x => x.UserName == user.UserName))
                {
                    var tenantId = TenantsService.DataSource.Any(x => x.Name == user.TenantName) ? TenantsService.DataSource.First(x => x.Name == user.TenantName).Id : TenantsService.DataSource.First().Id;
                    user.TenantId = tenantId;
                    var iResult = await UserManager.CreateAsync(user);
                    if (iResult.Succeeded)
                    {
                        await UserManager.AddToRolesAsync(user, new[] { RoleName.Basic });
                    }
                    else
                    {
                        Snackbar.Add($"{string.Join(',', iResult.Errors.Select(x => x.Description))}", Severity.Error);
                    }
                }
            }

            await _table.ReloadServerData();
            Snackbar.Add($"{ConstantString.ImportSuccess}", Severity.Info);
        }
        else
        {
            foreach (var msg in result.Errors)
            {
                Snackbar.Add($"{msg}", Severity.Error);
            }
        }
        _uploading = false;
    }
}