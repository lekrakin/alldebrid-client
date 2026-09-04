using AdbClient.Data.Data;
using Microsoft.AspNetCore.Identity;

namespace AdbClient.Service.Services;

public class Authentication(SignInManager<IdentityUser> signInManager, UserManager<IdentityUser> userManager, UserData userData)
{
    public async Task<IdentityResult> Register(string userName, string password)
    {
        var user = new IdentityUser(userName);

        var result = await userManager.CreateAsync(user, password);

        return result;
    }

    public async Task<SignInResult> Login(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return SignInResult.Failed;
        }

        var result = await signInManager.PasswordSignInAsync(userName, password, true, false);

        return result;
    }

    public async Task<IdentityUser?> GetUser()
    {
        return await userData.GetUser();
    }

    public async Task Logout()
    {
        await signInManager.SignOutAsync();
    }

    public async Task<IdentityResult> Update(string? newUserName, string? newPassword)
    {
        var user = await GetUser() ?? throw new Exception("No logged in user found");

        if (!string.IsNullOrWhiteSpace(newUserName))
        {
            user.UserName = newUserName;
            var updateResult = await userManager.UpdateAsync(user);

            if (!updateResult.Succeeded)
            {
                return updateResult;
            }
        }

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            return IdentityResult.Success;
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        return await userManager.ResetPasswordAsync(user, token, newPassword);
    }
}
